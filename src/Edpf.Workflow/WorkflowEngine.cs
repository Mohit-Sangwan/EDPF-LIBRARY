using System;
using System.Collections.Generic;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Tenancy;
using Edpf.Core.Guards;
using Edpf.Core.Time;

namespace Edpf.Workflow;

/// <summary>
/// A condition a transition depends on.
/// </summary>
/// <remarks>
/// A guard decides; it does not act. There is no mutable state in the
/// signature and no way to reach one, because a guard with side effects turns
/// "may this transition fire" into "fire it and find out" — and guards are
/// evaluated speculatively.
/// </remarks>
public interface IWorkflowGuard
{
    /// <summary>The name transitions reference.</summary>
    string Name { get; }

    /// <summary>
    /// Decides whether the transition may proceed.
    /// </summary>
    /// <param name="facts">The facts the caller supplied with the trigger.</param>
    /// <returns>
    /// Success to permit; a failure whose message is safe to surface to refuse.
    /// The message must not quote a fact — facts can be clinical.
    /// </returns>
    Result Evaluate(IReadOnlyDictionary<string, string> facts);
}

/// <summary>
/// Runs workflow instances (ADR-037; deferred there, built on the sponsor's
/// instruction).
/// </summary>
/// <remarks>
/// <para>
/// The engine's whole job is refusing things:
/// </para>
/// <list type="bullet">
///   <item>a trigger with no transition from the current state — refused, not ignored;</item>
///   <item>a trigger on an instance that has already finished;</item>
///   <item>a transition whose guard is not satisfied;</item>
///   <item>a fire against a stale version (ADR-020);</item>
///   <item>a fire against another tenant's instance.</item>
/// </list>
/// <para>
/// Guards are registered at construction and every guard a definition names
/// must be present, so a missing guard is a startup failure rather than a
/// runtime one (ADR-014). The alternative — treat an unknown guard as
/// satisfied — is the single worst default available here.
/// </para>
/// </remarks>
public sealed class WorkflowEngine
{
    private readonly WorkflowDefinition _definition;
    private readonly Dictionary<string, IWorkflowGuard> _guards;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IClock _clock;

    /// <summary>
    /// Composes an engine for one definition.
    /// </summary>
    /// <param name="definition">The workflow to run.</param>
    /// <param name="guards">Every guard the definition names.</param>
    /// <param name="tenantAccessor">Ambient tenant.</param>
    /// <param name="clock">Time source (Z.3 rule 4).</param>
    /// <exception cref="ArgumentNullException">Any dependency is null.</exception>
    /// <exception cref="ArgumentException">A transition names a guard that was not supplied.</exception>
    public WorkflowEngine(
        WorkflowDefinition definition,
        IReadOnlyList<IWorkflowGuard> guards,
        ITenantContextAccessor tenantAccessor,
        IClock clock)
    {
        _definition = Guard.NotNull(definition, nameof(definition));
        _tenantAccessor = Guard.NotNull(tenantAccessor, nameof(tenantAccessor));
        _clock = Guard.NotNull(clock, nameof(clock));
        Guard.NotNull(guards, nameof(guards));

        _guards = new Dictionary<string, IWorkflowGuard>(StringComparer.Ordinal);
        foreach (IWorkflowGuard guard in guards)
        {
            _guards[guard.Name] = guard;
        }

        foreach (WorkflowTransition transition in definition.Transitions)
        {
            if (transition.GuardName is not null && !_guards.ContainsKey(transition.GuardName))
            {
                throw new ArgumentException(
                    "A transition names a guard that is not registered. Treating an unknown guard as "
                    + "satisfied would make a missing condition indistinguishable from a met one.",
                    nameof(guards));
            }
        }
    }

    /// <summary>Starts a new instance under the ambient tenant.</summary>
    /// <param name="instanceId">The id to give it.</param>
    /// <returns>The instance, or a tenant refusal.</returns>
    public Result<WorkflowInstance> Start(Guid instanceId)
    {
        ITenantContext? tenant = _tenantAccessor.Current;
        if (tenant is null || tenant.TenantId == Guid.Empty)
        {
            return Result.Failure<WorkflowInstance>(TenantRefusal());
        }

        return new WorkflowInstance(_definition, instanceId, tenant.TenantId);
    }

    /// <summary>
    /// Advances an instance by firing a trigger.
    /// </summary>
    /// <param name="instance">The instance to advance.</param>
    /// <param name="trigger">The trigger to fire.</param>
    /// <param name="facts">Facts for any guard on the matching transition.</param>
    /// <param name="actor">Who fired it, for the history.</param>
    /// <param name="expectedVersion">
    /// The version the caller last read. A mismatch is a concurrency conflict,
    /// not a retry — somebody else moved this instance while the caller was
    /// deciding, and the decision may no longer apply.
    /// </param>
    /// <returns>The instance, advanced, or a failure explaining the refusal.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public Result<WorkflowInstance> Advance(
        WorkflowInstance instance,
        string trigger,
        IReadOnlyDictionary<string, string> facts,
        string actor,
        int expectedVersion)
    {
        Guard.NotNull(instance, nameof(instance));
        Guard.NotNullOrWhiteSpace(trigger, nameof(trigger));
        Guard.NotNull(facts, nameof(facts));
        Guard.NotNullOrWhiteSpace(actor, nameof(actor));

        ITenantContext? tenant = _tenantAccessor.Current;
        if (tenant is null || tenant.TenantId != instance.TenantId)
        {
            return Result.Failure<WorkflowInstance>(TenantRefusal());
        }

        if (instance.Version != expectedVersion)
        {
            return Result.Failure<WorkflowInstance>(new Error(
                ErrorCodes.ConcurrencyConflict,
                "The workflow moved while this decision was being made.",
                ErrorCategory.Concurrency));
        }

        if (_definition.IsTerminal(instance.CurrentState))
        {
            return Result.Failure<WorkflowInstance>(new Error(
                ErrorCodes.ValidationFailed,
                "The workflow has finished and accepts no further triggers.",
                ErrorCategory.Validation));
        }

        WorkflowTransition? transition = _definition.Find(instance.CurrentState, trigger);
        if (transition is null)
        {
            // Refused, not ignored. A trigger that silently does nothing is
            // indistinguishable from one that worked, and the caller will
            // report the workflow as stuck rather than as misused.
            return Result.Failure<WorkflowInstance>(new Error(
                ErrorCodes.ValidationFailed,
                "No transition leaves the current state on that trigger.",
                ErrorCategory.Validation));
        }

        if (transition.GuardName is not null)
        {
            Result permitted = _guards[transition.GuardName].Evaluate(facts);
            if (permitted.IsFailure)
            {
                return Result.Failure<WorkflowInstance>(permitted.Error!);
            }
        }

        // Normalised for the same reason instants are normalised everywhere
        // else: what is written must equal what was recorded, whichever engine
        // the history eventually lands in (ADR-036).
        var record = new WorkflowTransitionRecord(
            instance.CurrentState,
            trigger,
            transition.To,
            actor,
            StorableInstant.Normalize(_clock.UtcNow));

        instance.Apply(record);
        return instance;
    }

    private static Error TenantRefusal() => new(
        ErrorCodes.TenantScopeViolation,
        "The requested resource was not found.",
        ErrorCategory.NotFound);
}
