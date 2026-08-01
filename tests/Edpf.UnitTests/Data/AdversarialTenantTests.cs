using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;
using Edpf.Abstractions.Tenancy;
using Edpf.Data.Dialects;
using Edpf.Data.Query;

namespace Edpf.UnitTests.Data;

/// <summary>
/// Phase 10 §⑤: attempt cross-tenant access through **every** repository
/// entry point and assert every route is blocked. This is a security boundary
/// and is tested adversarially, not merely unit-tested (Phase 10 §⑥).
/// </summary>
public sealed class AdversarialTenantTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static TenantDescriptor ContextFor(Guid tenantId) => new(
        tenantId, "t", "in-south-1", TenantIsolationMode.SharedSchema, Guid.NewGuid());

    private static QueryCompiler Compiler => new(new SqlServerDialect(), new TestPatientMetadata());

    private static string TenantParameterOf(CompiledQuery query)
        => query.Parameters["tenantId"]!.ToString()!;

    // ── Route 1: no tenant resolved at all ─────────────────────────────────

    [Fact]
    public void Route1_NoTenantContext_IsRefusedNotTreatedAsAllTenants()
    {
        Result<CompiledQuery> result = Compiler.CompilePaged(
            Specification<object>.Create(), tenant: null, new PageRequest(1, 10));

        // The failure mode that matters: an unresolved tenant must never mean
        // "every tenant". It is refused, and refused as 404 (existence is not
        // disclosed across the boundary).
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.TenantScopeViolation, result.Error!.Code);
        Assert.Equal(ErrorCategory.NotFound, result.Error.Category);
    }

    [Fact]
    public void Route1b_NoTenantContextOnKeysetPath_IsAlsoRefused()
    {
        Result<CompiledQuery> result = Compiler.CompileKeyset(
            Specification<object>.Create().OrderBy("FamilyName"),
            tenant: null,
            cursorValues: [],
            pageSize: 10);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.TenantScopeViolation, result.Error!.Code);
    }

    // ── Route 2: the tenant predicate is unavoidable ───────────────────────

    [Fact]
    public void Route2_EmptySpecification_StillCarriesTheTenantPredicate()
    {
        Result<CompiledQuery> result = Compiler.CompilePaged(
            Specification<object>.Create(), ContextFor(TenantA), new PageRequest(1, 10));

        Assert.Contains("[TenantId] = @tenantId", result.Value.Sql, StringComparison.Ordinal);
        Assert.Equal(TenantA.ToString(), TenantParameterOf(result.Value));
    }

    // ── Route 3: filtering on TenantId directly ────────────────────────────

    [Fact]
    public void Route3_FilteringOnAnotherTenantId_DoesNotOverrideTheScope()
    {
        // A caller naming TenantId in their own filter adds a predicate; it
        // cannot replace the framework's, so the result is A AND B — which
        // matches nothing rather than crossing the boundary.
        Result<CompiledQuery> result = Compiler.CompilePaged(
            Specification<object>.Create().Where("TenantId", FilterOperator.Equal, TenantB),
            ContextFor(TenantA),
            new PageRequest(1, 10));

        Assert.True(result.IsSuccess);
        Assert.Equal(TenantA.ToString(), TenantParameterOf(result.Value));
        Assert.Equal(TenantB, result.Value.Parameters["p0"]);
        Assert.Contains("[TenantId] = @tenantId AND", result.Value.Sql, StringComparison.Ordinal);
    }

    // ── Route 4: OR-ing the tenant predicate away ──────────────────────────

    [Fact]
    public void Route4_OrClause_CannotEscapeTheTenantPredicate()
    {
        // The classic escape attempt: OR something-always-true. The tenant
        // predicate is a sibling AND at the top level, so no caller-supplied
        // OR can widen past it.
        var alwaysTrue = new ComparisonNode("Id", FilterOperator.IsNotNull, []);
        Result<CompiledQuery> result = Compiler.CompilePaged(
            Specification<object>.Create()
                .Where("FamilyName", FilterOperator.Equal, "Smith")
                .Or(alwaysTrue),
            ContextFor(TenantA),
            new PageRequest(1, 10));

        Assert.True(result.IsSuccess);

        // The caller's whole tree is parenthesised and ANDed after the tenant
        // predicate, so the OR is contained.
        Assert.StartsWith(
            "SELECT", result.Value.Sql, StringComparison.Ordinal);
        Assert.Contains("[TenantId] = @tenantId AND", result.Value.Sql, StringComparison.Ordinal);
        Assert.Contains(" OR ", result.Value.Sql, StringComparison.Ordinal);

        int tenantIndex = result.Value.Sql.IndexOf("[TenantId] = @tenantId", StringComparison.Ordinal);
        int orIndex = result.Value.Sql.IndexOf(" OR ", StringComparison.Ordinal);
        Assert.True(tenantIndex < orIndex, "The tenant predicate must precede and contain any caller OR.");
    }

    // ── Route 5: projection ────────────────────────────────────────────────

    [Fact]
    public void Route5_ProjectionOfNonProjectableField_IsDenied()
    {
        Result<CompiledQuery> result = Compiler.CompilePaged(
            Specification<object>.Create().Select("InternalRiskScore"),
            ContextFor(TenantA),
            new PageRequest(1, 10));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.FieldAccessDenied, result.Error!.Code);
    }

    [Fact]
    public void Route5b_ProjectionStillCarriesTheTenantPredicate()
    {
        Result<CompiledQuery> result = Compiler.CompilePaged(
            Specification<object>.Create().Select("GivenName", "FamilyName"),
            ContextFor(TenantA),
            new PageRequest(1, 10));

        Assert.Contains("[TenantId] = @tenantId", result.Value.Sql, StringComparison.Ordinal);
    }

    // ── Route 6: sorting ───────────────────────────────────────────────────

    [Fact]
    public void Route6_SortOnEncryptedField_IsRejected()
    {
        Result<CompiledQuery> result = Compiler.CompilePaged(
            Specification<object>.Create().OrderBy("MedicalRecordNumber"),
            ContextFor(TenantA),
            new PageRequest(1, 10));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.InvalidFilter, result.Error!.Code);
    }

    // ── Route 7: filtering an encrypted field ──────────────────────────────

    [Fact]
    public void Route7_FilterOnEncryptedField_IsRejectedWithoutNamingAlternatives()
    {
        Result<CompiledQuery> result = Compiler.CompilePaged(
            Specification<object>.Create().Where("MedicalRecordNumber", FilterOperator.Equal, "MRN-1"),
            ContextFor(TenantA),
            new PageRequest(1, 10));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.InvalidFilter, result.Error!.Code);

        // The message must not enumerate the filterable fields — that would
        // turn an error into a schema-discovery oracle (§10.2).
        Assert.DoesNotContain("GivenName", result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("FamilyName", result.Error.Message, StringComparison.Ordinal);
    }

    // ── Route 8: keyset cursor forged from another tenant's page ───────────

    [Fact]
    public void Route8_ForgedCursor_StillCarriesTheTenantPredicate()
    {
        Result<CompiledQuery> result = Compiler.CompileKeyset(
            Specification<object>.Create().OrderBy("FamilyName"),
            ContextFor(TenantA),
            cursorValues: ["Zzzz", Guid.NewGuid()],
            pageSize: 10);

        Assert.True(result.IsSuccess);
        Assert.Contains("[TenantId] = @tenantId", result.Value.Sql, StringComparison.Ordinal);
        Assert.Equal(TenantA.ToString(), TenantParameterOf(result.Value));
    }

    // ── Route 9: soft-deleted rows ─────────────────────────────────────────

    [Fact]
    public void Route9_SoftDeleteFilter_IsAppliedByDefault()
    {
        Result<CompiledQuery> result = Compiler.CompilePaged(
            Specification<object>.Create(), ContextFor(TenantA), new PageRequest(1, 10));

        Assert.Contains("[IsDeleted] = 0", result.Value.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Route9b_IncludingDeleted_RequiresAnAuditReason()
    {
        // The escape exists, but it is explicit and it is recorded.
        Assert.Throws<ArgumentException>(
            () => Specification<object>.Create().IncludingDeleted("  "));

        Specification<object> spec = Specification<object>.Create()
            .IncludingDeleted("legal hold review, ticket LEG-4417");

        Assert.True(spec.IncludeDeleted);
        Assert.Equal("legal hold review, ticket LEG-4417", spec.DeletedAccessReason);

        Result<CompiledQuery> result = Compiler.CompilePaged(
            spec, ContextFor(TenantA), new PageRequest(1, 10));

        // The soft-delete *predicate* is lifted (the column still appears in
        // the projection, which is correct); tenant scope emphatically is not.
        Assert.DoesNotContain("[IsDeleted] = 0", result.Value.Sql, StringComparison.Ordinal);
        Assert.Contains("[TenantId] = @tenantId", result.Value.Sql, StringComparison.Ordinal);
    }

    // ── Route 10: every tenant sees its own scope, never a shared one ──────

    [Theory]
    [InlineData("aaaaaaaa-0000-0000-0000-000000000001")]
    [InlineData("bbbbbbbb-0000-0000-0000-000000000002")]
    public void Route10_EachTenant_BindsItsOwnIdentifier(string tenantId)
    {
        var tenant = Guid.Parse(tenantId);

        Result<CompiledQuery> result = Compiler.CompilePaged(
            Specification<object>.Create(), ContextFor(tenant), new PageRequest(1, 10));

        Assert.Equal(tenant.ToString(), TenantParameterOf(result.Value));
    }
}
