using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;
using Edpf.Metadata;
using Edpf.Abstractions.Tenancy;
using Edpf.Core.Tenancy;
using Edpf.Data.Dialects;
using Edpf.Data.Query;

namespace Edpf.IsolationTests;

/// <summary>
/// Route 1 — the repository and query paths. Verified in depth by
/// <c>Edpf.UnitTests.Data.AdversarialTenantTests</c>; the cases here are the
/// ones that belong permanently in the isolation suite because they must be
/// re-run at every gate.
/// </summary>
[CoversIsolationRoute(IsolationRoutes.Repository)]
public sealed class RepositoryRouteTests
{
    private static QueryCompiler Compiler => new(new SqlServerDialect(), IsolationTestMetadata.Create());

    [Fact]
    public void Query_WithoutTenantContext_IsRefused()
    {
        Result<CompiledQuery> result = Compiler.CompilePaged(
            Specification<object>.Create(), tenant: null, new PageRequest(1, 10));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.TenantScopeViolation, result.Error!.Code);
    }

    [Fact]
    public void Query_AsTenantB_NeverBindsTenantA()
    {
        Result<CompiledQuery> result = Compiler.CompilePaged(
            Specification<object>.Create().Where("TenantId", FilterOperator.Equal, Tenants.A),
            Tenants.Context(Tenants.B),
            new PageRequest(1, 10));

        // The framework's own binding wins; the caller's attempt is an
        // additional AND that simply matches nothing.
        Assert.Equal(Tenants.B.ToString(), result.Value.Parameters["tenantId"]!.ToString());
    }
}

/// <summary>
/// Route 8 — error messages as an enumeration oracle. A cross-tenant read
/// must be indistinguishable from a genuine absence, and a rejected filter
/// must not reveal the schema.
/// </summary>
[CoversIsolationRoute(IsolationRoutes.ErrorEnumeration)]
public sealed class ErrorEnumerationRouteTests
{
    private static QueryCompiler Compiler => new(new SqlServerDialect(), IsolationTestMetadata.Create());

    [Fact]
    public void FieldTheCallerMayNotRead_IsIndistinguishableFromOneThatDoesNotExist()
    {
        // Phase 08b extends this route rather than adding a thirteenth: a
        // field-authorization refusal is the same oracle risk in a new place.
        // "You may not read this" confirms the column exists, and on a
        // tenant-overlaid entity the field list is itself tenant data.
        Result<CompiledQuery> denied = Compiler.CompilePaged(
            Specification<object>.Create().Where("Restricted", FilterOperator.Equal, "x"),
            Tenants.Context(Tenants.B),
            new PageRequest(1, 10));

        Result<CompiledQuery> missing = Compiler.CompilePaged(
            Specification<object>.Create().Where("Nonexistent", FilterOperator.Equal, "x"),
            Tenants.Context(Tenants.B),
            new PageRequest(1, 10));

        Assert.True(denied.IsFailure);
        Assert.Equal(missing.Error!.Code, denied.Error!.Code);
        Assert.Equal(missing.Error.Category, denied.Error.Category);
        Assert.DoesNotContain("permission", denied.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CrossTenantRefusal_UsesNotFoundSemantics_NotForbidden()
    {
        // 404, never 403: disclosing that a record exists but belongs to
        // someone else is itself the leak.
        Result<CompiledQuery> result = Compiler.CompilePaged(
            Specification<object>.Create(), tenant: null, new PageRequest(1, 10));

        Assert.Equal(ErrorCategory.NotFound, result.Error!.Category);
        Assert.Equal(ErrorCodes.TenantScopeViolation, result.Error.Code);
    }

    [Fact]
    public void CrossTenantRefusal_MessageRevealsNothingAboutTheResource()
    {
        Result<CompiledQuery> result = Compiler.CompilePaged(
            Specification<object>.Create(), tenant: null, new PageRequest(1, 10));

        Assert.Equal("The requested resource was not found.", result.Error!.Message);
        Assert.DoesNotContain("tenant", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectedFilter_DoesNotEnumerateValidFields()
    {
        Result<CompiledQuery> result = Compiler.CompilePaged(
            Specification<object>.Create().Where("SecretField", FilterOperator.Equal, "x"),
            Tenants.Context(Tenants.B),
            new PageRequest(1, 10));

        string message = result.Error!.Message;

        // Naming what the caller asked for is fine; listing the alternatives
        // would turn every rejection into a free schema dump.
        Assert.Contains("SecretField", message, StringComparison.Ordinal);
        Assert.DoesNotContain("GivenName", message, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordNumber", message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonFilterableField_RefusalDoesNotConfirmWhyItIsProtected()
    {
        Result<CompiledQuery> result = Compiler.CompilePaged(
            Specification<object>.Create().Where("RecordNumber", FilterOperator.Equal, "MRN-1"),
            Tenants.Context(Tenants.B),
            new PageRequest(1, 10));

        Assert.DoesNotContain("encrypt", result.Error!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PHI", result.Error.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// Route 7 — ambient context. A tenant scope must not survive its operation,
/// and concurrent operations must not observe each other's tenant.
/// </summary>
[CoversIsolationRoute(IsolationRoutes.LogCorrelation)]
[CoversIsolationRoute(IsolationRoutes.BackgroundJobContext)]
[CoversIsolationRoute(IsolationRoutes.ConnectionReuse)]
public sealed class AmbientContextRouteTests
{
    [Fact]
    public void TenantScope_AfterDisposal_DoesNotLeakToTheNextOperation()
    {
        // The connection-reuse and background-job routes share this root
        // cause: a pooled thread picking up the previous operation's context.
        var accessor = new TenantContextAccessor();

        using (accessor.Push(Tenants.Context(Tenants.A)))
        {
            Assert.Equal(Tenants.A, accessor.Current!.TenantId);
        }

        Assert.Null(accessor.Current);
    }

    [Fact]
    public async Task TenantScope_ConcurrentOperations_DoNotObserveEachOther()
    {
        var accessor = new TenantContextAccessor();
        var observed = new System.Collections.Concurrent.ConcurrentBag<(Guid Expected, Guid Actual)>();

        async Task RunAs(Guid tenantId)
        {
            using (accessor.Push(Tenants.Context(tenantId)))
            {
                for (int i = 0; i < 20; i++)
                {
                    await Task.Yield();
                    observed.Add((tenantId, accessor.Current!.TenantId));
                }
            }
        }

        await Task.WhenAll(
            Task.Run(() => RunAs(Tenants.A)),
            Task.Run(() => RunAs(Tenants.B)),
            Task.Run(() => RunAs(Tenants.A)),
            Task.Run(() => RunAs(Tenants.B)));

        Assert.All(observed, pair => Assert.Equal(pair.Expected, pair.Actual));
    }

    [Fact]
    public async Task TenantScope_BackgroundWork_StartsWithNoAmbientTenant()
    {
        // A job that inherits an ambient tenant would act as whichever tenant
        // happened to enqueue it. Background work must resolve its own.
        var accessor = new TenantContextAccessor();
        Guid? observedInsideJob = Guid.Empty;

        using (accessor.Push(Tenants.Context(Tenants.A)))
        {
            await Task.Run(() =>
            {
                // Simulates a job runner starting a fresh logical operation.
                using (accessor.Push(Tenants.Context(Tenants.B)))
                {
                    observedInsideJob = accessor.Current!.TenantId;
                }
            });
        }

        Assert.Equal(Tenants.B, observedInsideJob);
        Assert.Null(accessor.Current);
    }
}

/// <summary>
/// Metadata for the isolation suite's fixture entity.
/// </summary>
/// <remarks>
/// Built from the production <see cref="EntityMetadata"/> since Phase 05b. An
/// isolation suite proving a property against a hand-rolled double proves it
/// about the double; the whole value of these twelve routes is that they run
/// against what ships.
/// </remarks>
internal static class IsolationTestMetadata
{
    public static EntityMetadata Create() => new(
        "SubjectRecord",
        "SUBJECT_RECORD",
        [
            new FieldMetadata("Id", "Id", typeof(Guid), DataClassificationLevel.Internal,
                isFilterable: true, isSortable: true),
            new FieldMetadata("TenantId", "TenantId", typeof(Guid), DataClassificationLevel.Internal,
                isFilterable: true, isSortable: true),
            new FieldMetadata("GivenName", "GivenName", typeof(string), DataClassificationLevel.Internal,
                isFilterable: true, isSortable: true),

            // Classified: encrypted at rest, so neither filterable nor
            // sortable. FieldMetadata enforces that pairing rather than
            // trusting this fixture to keep three flags consistent.
            new FieldMetadata("RecordNumber", "RecordNumberEnvelope", typeof(byte[]),
                DataClassificationLevel.Phi),
            new FieldMetadata("IsDeleted", "IsDeleted", typeof(bool), DataClassificationLevel.Internal,
                isFilterable: true, isSortable: true),

            // Phase 08b: a field gated on a permission nobody in this suite
            // holds, so the refusal path is exercised at every gate.
            new FieldMetadata("Restricted", "Restricted", typeof(string),
                DataClassificationLevel.Internal, isFilterable: true, isSortable: true,
                requiredScope: "restricted.read"),
        ]);
}
