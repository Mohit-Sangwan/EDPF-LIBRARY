using Edpf.Abstractions.Identity;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;
using Edpf.Abstractions.Tenancy;
using Edpf.Data.Dialects;
using Edpf.Data.Query;
using Edpf.Metadata;

namespace Edpf.UnitTests.Data;

/// <summary>
/// Phase 08b — field-level authorization (TST-AUTHZ-FIELD).
/// </summary>
/// <remarks>
/// Phase 05b added <c>IFieldMetadata.RequiredScope</c> and nothing read it: a
/// field could declare "you need this permission to see me" and the query
/// compiler would project it to anyone. These tests are the enforcement.
/// </remarks>
public sealed class FieldAuthorizationTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private static ITenantContext Context => new TenantDescriptor(
        Tenant, "tenant-a", "eu-west", TenantIsolationMode.SharedSchema, Guid.NewGuid());

    private static EntityMetadata Metadata() => new(
        "SubjectRecord",
        "SUBJECT_RECORD",
        [
            new FieldMetadata("Id", "Id", typeof(Guid), DataClassificationLevel.Internal,
                isFilterable: true, isSortable: true),
            new FieldMetadata("TenantId", "TenantId", typeof(Guid), DataClassificationLevel.Internal,
                isFilterable: true, isSortable: true),
            new FieldMetadata("DisplayLabel", "DisplayLabel", typeof(string),
                DataClassificationLevel.Internal, isFilterable: true, isSortable: true),
            new FieldMetadata("IsDeleted", "IsDeleted", typeof(bool), DataClassificationLevel.Internal,
                isFilterable: true, isSortable: true),

            // The protected field. Unclassified on purpose: this is about
            // authorization, not classification, and the two are independent
            // axes — a salary is not PHI but not everyone may read it.
            new FieldMetadata("Compensation", "Compensation", typeof(decimal),
                DataClassificationLevel.Internal, isFilterable: true, isSortable: true,
                requiredScope: "compensation.read"),
        ]);

    private static QueryCompiler CompilerFor(params string[] granted)
        => new(new SqlServerDialect(), Metadata(), new FieldPermissionSet(granted));

    private static Result<CompiledQuery> Compile(
        QueryCompiler compiler, ISpecification<object> specification)
        => compiler.CompilePaged(specification, Context, new PageRequest(1, 10));

    // ── the defect this phase closes ───────────────────────────────────────

    [Fact]
    public void ProtectedField_IsNotProjectedToACallerWithoutThePermission()
    {
        Result<CompiledQuery> result = Compile(CompilerFor(), Specification<object>.Create());

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain("Compensation", result.Value.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ProtectedField_IsProjectedToACallerHoldingThePermission()
    {
        Result<CompiledQuery> result = Compile(
            CompilerFor("compensation.read"), Specification<object>.Create());

        Assert.Contains("Compensation", result.Value.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void UnprotectedFields_AreStillProjected()
    {
        // Denying the whole query would make one protected column break every
        // default read for everyone below it.
        Result<CompiledQuery> result = Compile(CompilerFor(), Specification<object>.Create());

        Assert.Contains("DisplayLabel", result.Value.Sql, StringComparison.Ordinal);
    }

    // ── filtering is reading ───────────────────────────────────────────────

    [Fact]
    public void FilteringOnAProtectedField_IsRefused()
    {
        // WHERE Compensation > 100000 never projects the value, and a caller
        // who cannot see the column can still binary-search every value in
        // the table by watching which rows come back. Denying projection
        // while permitting filter is a disclosure control that discloses.
        Result<CompiledQuery> result = Compile(
            CompilerFor(),
            Specification<object>.Create().Where("Compensation", FilterOperator.GreaterThan, 100_000m));

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void FilteringOnAProtectedField_IsAllowedWithThePermission()
    {
        Result<CompiledQuery> result = Compile(
            CompilerFor("compensation.read"),
            Specification<object>.Create().Where("Compensation", FilterOperator.GreaterThan, 100_000m));

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain("100000", result.Value.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SortingOnAProtectedField_IsRefused()
    {
        // Same argument as filtering: an ORDER BY over a protected column
        // plus a walk across page boundaries reconstructs the ordering, and
        // an ordering over salaries is most of the salaries.
        Result<CompiledQuery> result = Compile(
            CompilerFor(), Specification<object>.Create().OrderBy("Compensation"));

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void ExplicitlyProjectingAProtectedField_IsRefused()
    {
        Result<CompiledQuery> result = Compile(
            CompilerFor(), Specification<object>.Create().Select("Compensation"));

        Assert.True(result.IsFailure);
    }

    // ── the refusal is not an oracle ───────────────────────────────────────

    [Fact]
    public void RefusalForAProtectedField_IsIdenticalToRefusalForAMissingOne()
    {
        // Distinguishing them turns the error into a schema oracle: "you may
        // not read this" confirms the column exists, which is often the fact
        // worth protecting.
        QueryCompiler compiler = CompilerFor();

        Error protectedField = Compile(
            compiler,
            Specification<object>.Create().Where("Compensation", FilterOperator.Equal, 1m)).Error!;

        Error missingField = Compile(
            compiler,
            Specification<object>.Create().Where("Nonexistent", FilterOperator.Equal, 1m)).Error!;

        Assert.Equal(missingField.Code, protectedField.Code);
        Assert.Equal(missingField.Category, protectedField.Category);

        // Same sentence shape, differing only in the name the caller supplied.
        Assert.Equal(
            missingField.Message.Replace("Nonexistent", "X", StringComparison.Ordinal),
            protectedField.Message.Replace("Compensation", "X", StringComparison.Ordinal));
    }

    [Fact]
    public void RefusalMessage_DoesNotMentionPermissionsOrAuthorization()
    {
        Error error = Compile(
            CompilerFor(),
            Specification<object>.Create().Where("Compensation", FilterOperator.Equal, 1m)).Error!;

        Assert.DoesNotContain("permission", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scope", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authoriz", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── failing closed ─────────────────────────────────────────────────────

    [Fact]
    public void CompilerConstructedWithoutPermissions_DeniesProtectedFields()
    {
        // The forgotten argument must fail in the direction that does not
        // disclose. A caller who omits permissions gets none, not all.
        var compiler = new QueryCompiler(new SqlServerDialect(), Metadata());

        Result<CompiledQuery> result = Compile(compiler, Specification<object>.Create());

        Assert.DoesNotContain("Compensation", result.Value.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void PermissionMatching_IsExact_WithNoPrefixMatching()
    {
        // Prefix matching is a classic quiet vulnerability: a caller granted
        // "compensation.read" would satisfy a field requiring
        // "compensation.readAll", and a caller granted "comp" would satisfy
        // everything beginning with those four letters. The bug is invisible
        // in review because the grant looks narrower than the requirement.
        var narrower = new FieldPermissionSet(["compensation.rea"]);
        var broader = new FieldPermissionSet(["compensation.read.extended"]);

        Assert.False(narrower.Grants("compensation.read"));
        Assert.False(broader.Grants("compensation.read"));
        Assert.True(new FieldPermissionSet(["compensation.read"]).Grants("compensation.read"));
    }

    [Fact]
    public void PermissionMatching_IsOrdinal_NotCultureSensitive()
    {
        // Under a Turkish culture a case-insensitive comparison makes "I" and
        // "ı" the same letter, and the same grant would authorize differently
        // in two regions (Phase 27).
        var permissions = new FieldPermissionSet(["FILE.read"]);

        Assert.False(permissions.Grants("file.read"));
        Assert.True(permissions.Grants("FILE.read"));
    }

    [Fact]
    public void EmptyOrWhitespacePermission_GrantsNothing()
    {
        // A field declaring an empty required scope is unprotected by design;
        // a caller holding an empty grant must not satisfy a real requirement.
        var permissions = new FieldPermissionSet(["", "   ", "real.permission"]);

        Assert.False(permissions.Grants(string.Empty));
        Assert.False(permissions.Grants("   "));
        Assert.True(permissions.Grants("real.permission"));
        Assert.Single(permissions.Granted);
    }

    [Fact]
    public void CallerWhoMayReadNothing_IsRefusedRatherThanGivenAnEmptySelect()
    {
        // A SELECT with no columns is not valid SQL, and a query that
        // silently returns empty rows would look like "no data" rather than
        // "no access".
        var metadata = new EntityMetadata(
            "Locked",
            "LOCKED",
            [
                new FieldMetadata("Secret", "Secret", typeof(string), DataClassificationLevel.Internal,
                    requiredScope: "nobody.has.this"),
            ]);

        var compiler = new QueryCompiler(new SqlServerDialect(), metadata);

        Result<CompiledQuery> result = compiler.CompilePaged(
            Specification<object>.Create(), Context, new PageRequest(1, 10));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.FieldAccessDenied, result.Error!.Code);
    }
}
