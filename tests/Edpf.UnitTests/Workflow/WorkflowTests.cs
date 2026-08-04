using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Tenancy;
using Edpf.Core.Tenancy;
using Edpf.UnitTests.TestDoubles;
using Edpf.Workflow;

namespace Edpf.UnitTests.Workflow;

/// <summary>
/// The workflow platform. Almost every test here is about something the
/// definition or the engine <em>refuses</em>, which is the point: a state
/// machine that accepts anything is a dictionary with extra steps.
/// </summary>
public sealed class WorkflowTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid InstanceId = Guid.Parse("11111111-0000-0000-0000-000000000001");

    private readonly TenantContextAccessor _tenants = new();
    private readonly FakeClock _clock = new();

    private static WorkflowDefinition Referral() => new(
        "referral",
        "Draft",
        ["Accepted", "Rejected", "Withdrawn"],
        [
            new WorkflowTransition("Draft", "submit", "Submitted"),
            new WorkflowTransition("Draft", "withdraw", "Withdrawn"),
            new WorkflowTransition("Submitted", "accept", "Accepted", "clinician-signed"),
            new WorkflowTransition("Submitted", "reject", "Rejected"),
            new WorkflowTransition("Submitted", "withdraw", "Withdrawn"),
        ]);

    private WorkflowEngine CreateEngine(IWorkflowGuard? guard = null)
        => new(Referral(), [guard ?? new SignatureGuard()], _tenants, _clock);

    private IDisposable ActAs(Guid tenantId)
        => _tenants.Push(new TenantDescriptor(
            tenantId, "tenant", "eu-west", TenantIsolationMode.SharedSchema, Guid.NewGuid()));

    private static readonly Dictionary<string, string> NoFacts = [];

    private static Dictionary<string, string> Signed() => new() { ["signature"] = "present" };

    // ── the definition validates its own shape ────────────────────────────

    [Fact]
    public void Definition_RefusesTwoTransitionsSharingAStateAndTrigger()
    {
        // Which one fires would depend on declaration order. ADR-027 refuses
        // exactly this ambiguity in decision tables; a workflow is no different.
        Assert.Throws<ArgumentException>(() => new WorkflowDefinition(
            "w",
            "A",
            ["B"],
            [
                new WorkflowTransition("A", "go", "B"),
                new WorkflowTransition("A", "go", "A"),
            ]));
    }

    [Fact]
    public void Definition_RefusesAnUnreachableState()
    {
        // "Archived" exists and nothing can ever reach it. Somebody will
        // eventually write a report that expects instances to be in it.
        Assert.Throws<ArgumentException>(() => new WorkflowDefinition(
            "w",
            "A",
            ["B", "Archived"],
            [new WorkflowTransition("A", "go", "B")]));
    }

    [Fact]
    public void Definition_RefusesANonTerminalStateWithNoWayOut()
    {
        // Every instance that reaches B is trapped, and the symptom is a queue
        // that stops draining several weeks later.
        Assert.Throws<ArgumentException>(() => new WorkflowDefinition(
            "w",
            "A",
            ["C"],
            [new WorkflowTransition("A", "go", "B")]));
    }

    [Fact]
    public void Definition_RefusesATerminalStateWithAnExit()
    {
        Assert.Throws<ArgumentException>(() => new WorkflowDefinition(
            "w",
            "A",
            ["B"],
            [
                new WorkflowTransition("A", "go", "B"),
                new WorkflowTransition("B", "undo", "A"),
            ]));
    }

    [Fact]
    public void Definition_AcceptsAWellFormedWorkflow()
    {
        WorkflowDefinition definition = Referral();

        Assert.Equal("Draft", definition.InitialState);
        Assert.Contains("Submitted", definition.States);
        Assert.True(definition.IsTerminal("Accepted"));
        Assert.False(definition.IsTerminal("Submitted"));
    }

    // ── composition-time failures ─────────────────────────────────────────

    [Fact]
    public void Engine_RefusesADefinitionNamingAnUnregisteredGuard()
    {
        // Treating an unknown guard as satisfied would make a missing
        // condition indistinguishable from a met one — the worst default
        // available here.
        Assert.Throws<ArgumentException>(
            () => new WorkflowEngine(Referral(), [], _tenants, _clock));
    }

    // ── the engine refuses ────────────────────────────────────────────────

    [Fact]
    public void Start_WithNoResolvedTenant_IsRefused()
    {
        Result<WorkflowInstance> started = CreateEngine().Start(InstanceId);

        Assert.True(started.IsFailure);
        Assert.Equal(ErrorCodes.TenantScopeViolation, started.Error!.Code);
    }

    [Fact]
    public void Advance_AgainstAnotherTenantsInstance_IsRefused()
    {
        WorkflowEngine engine = CreateEngine();

        WorkflowInstance instance;
        using (ActAs(TenantA))
        {
            instance = engine.Start(InstanceId).Value;
        }

        using (ActAs(TenantB))
        {
            Result<WorkflowInstance> fired = engine.Advance(instance, "submit", NoFacts, "user", 0);

            Assert.True(fired.IsFailure);
            Assert.Equal(ErrorCodes.TenantScopeViolation, fired.Error!.Code);
        }

        Assert.Equal("Draft", instance.CurrentState);
    }

    [Fact]
    public void Advance_WithAnUndefinedTrigger_IsRefusedNotIgnored()
    {
        // A trigger that silently does nothing is indistinguishable from one
        // that worked, and the caller reports the workflow as stuck rather
        // than as misused.
        WorkflowEngine engine = CreateEngine();

        using (ActAs(TenantA))
        {
            WorkflowInstance instance = engine.Start(InstanceId).Value;

            Result<WorkflowInstance> fired = engine.Advance(instance, "accept", NoFacts, "user", 0);

            Assert.True(fired.IsFailure);
            Assert.Equal(ErrorCodes.ValidationFailed, fired.Error!.Code);
            Assert.Equal("Draft", instance.CurrentState);
        }
    }

    [Fact]
    public void Advance_OnAFinishedWorkflow_IsRefused()
    {
        WorkflowEngine engine = CreateEngine();

        using (ActAs(TenantA))
        {
            WorkflowInstance instance = engine.Start(InstanceId).Value;
            engine.Advance(instance, "withdraw", NoFacts, "user", 0);

            Assert.True(engine.Advance(instance, "submit", NoFacts, "user", 1).IsFailure);
            Assert.Equal("Withdrawn", instance.CurrentState);
        }
    }

    [Fact]
    public void Advance_WithAStaleVersion_IsAConcurrencyConflict()
    {
        // Two clinicians open the same referral; both click Approve. Exactly
        // one of them is acting on what they read (ADR-020).
        WorkflowEngine engine = CreateEngine();

        using (ActAs(TenantA))
        {
            WorkflowInstance instance = engine.Start(InstanceId).Value;
            engine.Advance(instance, "submit", NoFacts, "first", 0);

            Result<WorkflowInstance> second = engine.Advance(instance, "reject", NoFacts, "second", 0);

            Assert.True(second.IsFailure);
            Assert.Equal(ErrorCodes.ConcurrencyConflict, second.Error!.Code);
        }
    }

    [Fact]
    public void Advance_WithAnUnsatisfiedGuard_DoesNotTransition()
    {
        WorkflowEngine engine = CreateEngine();

        using (ActAs(TenantA))
        {
            WorkflowInstance instance = engine.Start(InstanceId).Value;
            engine.Advance(instance, "submit", NoFacts, "user", 0);

            Result<WorkflowInstance> accepted = engine.Advance(instance, "accept", NoFacts, "user", 1);

            Assert.True(accepted.IsFailure);
            Assert.Equal("Submitted", instance.CurrentState);
            Assert.Equal(1, instance.Version);
        }
    }

    [Fact]
    public void Advance_GuardRefusal_DoesNotQuoteTheFacts()
    {
        // Facts can be clinical. A guard's refusal reaches a user, so it says
        // what was required, never what was supplied.
        WorkflowEngine engine = CreateEngine();

        using (ActAs(TenantA))
        {
            WorkflowInstance instance = engine.Start(InstanceId).Value;
            engine.Advance(instance, "submit", NoFacts, "user", 0);

            Result<WorkflowInstance> accepted = engine.Advance(
                instance,
                "accept",
                new Dictionary<string, string> { ["signature"] = "forged-by-dr-smith" },
                "user",
                1);

            Assert.True(accepted.IsFailure);
            Assert.DoesNotContain("forged-by-dr-smith", accepted.Error!.Message, StringComparison.Ordinal);
        }
    }

    // ── the happy path, and its history ───────────────────────────────────

    [Fact]
    public void Advance_ThroughAWholeWorkflow_RecordsEveryStep()
    {
        WorkflowEngine engine = CreateEngine();

        using (ActAs(TenantA))
        {
            WorkflowInstance instance = engine.Start(InstanceId).Value;

            Assert.True(engine.Advance(instance, "submit", NoFacts, "reception", 0).IsSuccess);
            Assert.True(engine.Advance(instance, "accept", Signed(), "dr-jones", 1).IsSuccess);

            Assert.Equal("Accepted", instance.CurrentState);
            Assert.Equal(2, instance.Version);
            Assert.Equal(2, instance.History.Count);

            // The current state alone cannot answer "how did this get here".
            Assert.Equal("Draft", instance.History[0].From);
            Assert.Equal("submit", instance.History[0].Trigger);
            Assert.Equal("reception", instance.History[0].Actor);
            Assert.Equal("dr-jones", instance.History[1].Actor);
        }
    }

    [Fact]
    public void History_RecordsInstantsAtTheStorableResolution()
    {
        // Same reason as everywhere else: what is written must equal what was
        // recorded, whichever engine the history eventually lands in (ADR-036).
        WorkflowEngine engine = CreateEngine();
        _clock.UtcNow = new DateTimeOffset(2026, 8, 4, 9, 30, 0, TimeSpan.Zero).AddTicks(7);

        using (ActAs(TenantA))
        {
            WorkflowInstance instance = engine.Start(InstanceId).Value;
            engine.Advance(instance, "submit", NoFacts, "user", 0);

            Assert.Equal(0, instance.History[0].OccurredUtc.UtcTicks % 10);
        }
    }

    [Fact]
    public void Instance_CannotBeConstructedWithoutATenant()
    {
        Assert.Throws<ArgumentException>(() => new WorkflowInstance(Referral(), InstanceId, Guid.Empty));
    }

    private sealed class SignatureGuard : IWorkflowGuard
    {
        public string Name => "clinician-signed";

        public Result Evaluate(IReadOnlyDictionary<string, string> facts)
            => facts.TryGetValue("signature", out string? signature)
                && string.Equals(signature, "present", StringComparison.Ordinal)
                ? Result.Success()
                : Result.Failure(new Error(
                    ErrorCodes.ValidationFailed,
                    "A clinician signature is required before acceptance.",
                    ErrorCategory.Validation));
    }
}
