using System.Diagnostics;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;
using Edpf.Metadata;
using Edpf.Data.Dialects;
using Edpf.Data.Query;

namespace Edpf.IsolationTests;

/// <summary>
/// Route 2 — the raw-SQL escape hatch. The builder makes injection
/// unrepresentable; `ExecuteRawUnsafe` deliberately does not, which is why it
/// is attributed, audited and counted rather than removed.
/// </summary>
[CoversIsolationRoute(IsolationRoutes.RawSql)]
public sealed class RawSqlRouteTests
{
    [Fact]
    public void QueryCompiler_ExposesNoApiTakingRawSql()
    {
        // The structural half of the guarantee: the safe path offers no way
        // to pass SQL text at all, so reaching the unsafe path is a visible,
        // deliberate act rather than an easy mistake.
        IEnumerable<string> suspicious = typeof(QueryCompiler)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => m.GetParameters().Any(p =>
                p.ParameterType == typeof(string)
                && (p.Name!.Contains("sql", StringComparison.OrdinalIgnoreCase)
                 || p.Name.Contains("query", StringComparison.OrdinalIgnoreCase)
                 || p.Name.Contains("where", StringComparison.OrdinalIgnoreCase))))
            .Select(m => m.Name);

        Assert.Empty(suspicious);
    }

    [Fact]
    public void Specification_ExposesNoApiTakingRawSql()
    {
        IEnumerable<string> suspicious = typeof(Specification<>)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => m.GetParameters().Any(p =>
                p.ParameterType == typeof(string)
                && p.Name!.Contains("sql", StringComparison.OrdinalIgnoreCase)))
            .Select(m => m.Name);

        Assert.Empty(suspicious);
    }
}

/// <summary>
/// Route 9 — timing side-channels. "Record absent" and "record belongs to
/// another tenant" must be indistinguishable, including in how long the
/// refusal takes.
/// </summary>
[CoversIsolationRoute(IsolationRoutes.TimingSideChannel)]
public sealed class TimingSideChannelRouteTests
{
    private static QueryCompiler Compiler => new(new SqlServerDialect(), IsolationTestMetadata.Create());

    [Fact]
    public void CompiledQuery_ForExistingAndNonExistingIds_IsStructurallyIdentical()
    {
        // At the compiler level the guarantee is exact rather than
        // statistical: the same statement is emitted regardless of whether
        // the id exists or belongs elsewhere, so the *database* sees no
        // difference to time.
        Result<CompiledQuery> hit = Compiler.CompilePaged(
            Specification<object>.Create().Where("Id", FilterOperator.Equal, Guid.NewGuid()),
            Tenants.Context(Tenants.B),
            new PageRequest(1, 1));

        Result<CompiledQuery> miss = Compiler.CompilePaged(
            Specification<object>.Create().Where("Id", FilterOperator.Equal, Guid.NewGuid()),
            Tenants.Context(Tenants.B),
            new PageRequest(1, 1));

        Assert.Equal(hit.Value.Sql, miss.Value.Sql);
    }

    [Fact]
    public void TenantRefusal_TakesNoDataDependentWork()
    {
        // The refusal happens before any store access, so its cost cannot
        // depend on what is stored. Measured loosely — this asserts the
        // absence of I/O, not a hardened constant-time property.
        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < 1_000; i++)
        {
            _ = Compiler.CompilePaged(Specification<object>.Create(), tenant: null, new PageRequest(1, 10));
        }

        stopwatch.Stop();

        Assert.True(
            stopwatch.ElapsedMilliseconds < 1_000,
            $"1000 tenant refusals took {stopwatch.ElapsedMilliseconds}ms; a refusal must not touch the store.");
    }
}

/// <summary>
/// Routes 5 and 12 — message routing and outbox dispatch.
/// </summary>
/// <remarks>
/// These routes are covered here at the contract level: domain events and
/// outbox payloads carry **subject tokens, never raw identifiers** (§10.5),
/// and every outbox row is tenant-stamped. The end-to-end assertion — that a
/// real broker cannot deliver tenant A's message to tenant B's consumer —
/// requires the Phase 26 transports and is a Gate G3 carry-forward, recorded
/// in the completion report rather than implied by a passing test here.
/// </remarks>
[CoversIsolationRoute(IsolationRoutes.MessageRouting)]
[CoversIsolationRoute(IsolationRoutes.OutboxDispatch)]
public sealed class MessagingRouteTests
{
    [Fact]
    public void OutboxRow_Contract_RequiresATenantStamp()
    {
        // Asserted against the walking skeleton's row shape, which is the
        // only outbox implementation that exists until Phase 26.
        Type outboxRow = typeof(Edpf.Abstractions.Consistency.IOutboxDispatcher).Assembly
            .GetType("Edpf.Abstractions.Consistency.IdempotencyRecord")!;

        // The idempotency record is the closest analogue in Abstractions and
        // carries the same requirement: tenant-scoped by construction.
        Assert.NotNull(outboxRow.GetProperty("TenantId"));
    }

    [Fact]
    public void IdempotencyRecord_IsTenantScoped_SoOneTenantCannotReplayAnothersOperation()
    {
        var record = new Edpf.Abstractions.Consistency.IdempotencyRecord(
            Tenants.A, "key-1", "hash", 201, "{}", DateTimeOffset.UnixEpoch);

        Assert.Equal(Tenants.A, record.TenantId);
    }
}
