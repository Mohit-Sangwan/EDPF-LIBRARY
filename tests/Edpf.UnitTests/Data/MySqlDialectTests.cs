using Edpf.Abstractions.Data;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;
using Edpf.Abstractions.Tenancy;
using Edpf.Data.Dialects;
using Edpf.Data.Query;

namespace Edpf.UnitTests.Data;

/// <summary>
/// The fourth dialect (ADR-037 v1.0 scope). Where SQLite tested whether the
/// abstraction survived an engine that lacks features, MySQL tests whether it
/// survives an engine whose features mean something different.
/// </summary>
public sealed class MySqlDialectTests
{
    private static readonly MySqlDialect Dialect = new();

    private static ITenantContext Tenant => new TenantDescriptor(
        Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
        "tenant-a", "eu-west", TenantIsolationMode.SharedSchema, Guid.NewGuid());

    // ── the properties every dialect must hold ────────────────────────────

    [Fact]
    public void IllegalIdentifier_IsRejectedNotEscaped()
    {
        Assert.Throws<ArgumentException>(() => Dialect.QuoteIdentifier("Patients`; DROP TABLE X--"));
        Assert.Throws<ArgumentException>(() => Dialect.QuoteIdentifier("  "));
        Assert.Throws<ArgumentException>(() => Dialect.QuoteIdentifier(new string('a', 65)));
    }

    [Fact]
    public void LegalIdentifier_IsBacktickQuoted_NotDoubleQuoted()
    {
        // The other three dialects use ". MySQL only accepts that under
        // ANSI_QUOTES, which is not the default — so quoting here would depend
        // on a setting on somebody else's server.
        Assert.Equal("`MedicalRecordNumber`", Dialect.QuoteIdentifier("MedicalRecordNumber"));
    }

    [Fact]
    public void TenantPredicate_IsStillEmittedFirstAndUnconditionally()
    {
        var compiler = new QueryCompiler(Dialect, TestEntities.SubjectRecord());

        CompiledQuery query = compiler.CompilePaged(
            Specification<object>.Create(), Tenant, new PageRequest(1, 10)).Value;

        Assert.True(query.Sql.IndexOf("TenantId", StringComparison.Ordinal) > 0, "The tenant predicate is absent.");
        Assert.Contains("@tenantId", query.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void UnresolvedTenant_IsRefusedOnThisDialectToo()
    {
        var compiler = new QueryCompiler(Dialect, TestEntities.SubjectRecord());

        Result<CompiledQuery> result = compiler.CompilePaged(
            Specification<object>.Create(), tenant: null, new PageRequest(1, 10));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.TenantScopeViolation, result.Error!.Code);
    }

    [Fact]
    public void HostileFilterValue_NeverReachesTheSql()
    {
        var compiler = new QueryCompiler(Dialect, TestEntities.SubjectRecord());

        CompiledQuery query = compiler.CompilePaged(
            Specification<object>.Create()
                .Where("GivenName", FilterOperator.Equal, "'; DROP TABLE SUBJECT_RECORD; --"),
            Tenant,
            new PageRequest(1, 10)).Value;

        Assert.DoesNotContain("DROP TABLE", query.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'; DROP TABLE SUBJECT_RECORD; --", query.Parameters.Values.Select(v => v?.ToString()));
    }

    // ── where MySQL means something different ─────────────────────────────

    [Fact]
    public void Concat_UsesTheFunction_BecausePipesMeanLogicalOrHere()
    {
        // The one that matters. The base emits `a || b`, which MySQL parses as
        // logical OR by default: no error, no exception, just 0 or 1 written
        // into a column that expected a string.
        Assert.Equal("CONCAT(`a`, `b`)", Dialect.Concat(["`a`", "`b`"]));
    }

    [Fact]
    public void IdentityRetrieval_Throws_RatherThanReturningAConnectionScopedValue()
    {
        // LAST_INSERT_ID() is right almost always, which is the worst property
        // a correctness primitive can have. Throwing keeps the capability flag
        // load-bearing instead of decorative.
        NotSupportedException thrown = Assert.Throws<NotSupportedException>(
            () => Dialect.IdentityRetrievalClause());

        Assert.Contains("SupportsIdentityRetrieval", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Capabilities_AndDialect_AgreeAboutIdentityRetrieval()
    {
        // The failure this catches: a capability saying yes while the dialect
        // throws, or the reverse. Either one turns negotiation into a lie, and
        // nothing else in the system cross-checks the two.
        var capabilities = new MySqlCapabilities();

        Assert.False(capabilities.SupportsIdentityRetrieval);
        Assert.Throws<NotSupportedException>(() => Dialect.IdentityRetrievalClause());
    }

    [Fact]
    public void UtcNow_CarriesMicroseconds_NotWholeSeconds()
    {
        // UTC_TIMESTAMP without an argument truncates to seconds. That is the
        // shape of the ADR-036 defect: a hashed timestamp the store never held.
        Assert.Equal("UTC_TIMESTAMP(6)", Dialect.UtcNowExpression());
    }

    [Fact]
    public void JsonValue_UsesJsonExtract()
    {
        Assert.Equal("JSON_EXTRACT(`Payload`, @path)", Dialect.JsonValue("`Payload`", "path"));
    }

    [Fact]
    public void Pagination_UsesLimitOffset()
    {
        Assert.Equal("LIMIT @take OFFSET @skip", Dialect.PaginationClause("skip", "take"));
    }

    // ── the abstraction across all four ───────────────────────────────────

    [Fact]
    public void FourDialects_AgreeOnStructureAndDifferOnlyWhereDeclared()
    {
        SqlDialectBase[] dialects =
        [
            new SqlServerDialect(),
            new PostgreSqlDialect(),
            new SqliteDialect(),
            new MySqlDialect(),
        ];

        foreach (SqlDialectBase dialect in dialects)
        {
            var compiler = new QueryCompiler(dialect, TestEntities.SubjectRecord());

            CompiledQuery query = compiler.CompilePaged(
                Specification<object>.Create().Where("GivenName", FilterOperator.Equal, "x"),
                Tenant,
                new PageRequest(1, 10)).Value;

            Assert.Contains("SELECT", query.Sql, StringComparison.Ordinal);
            Assert.Contains("ORDER BY", query.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain("'x'", query.Sql, StringComparison.Ordinal);
            Assert.True(query.Parameters.ContainsKey("tenantId"), dialect.ProviderName);
        }
    }

    [Fact]
    public void EveryDialect_HasADistinctProviderName()
    {
        string[] names =
        [
            new SqlServerDialect().ProviderName,
            new PostgreSqlDialect().ProviderName,
            new SqliteDialect().ProviderName,
            new MySqlDialect().ProviderName,
        ];

        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void HalfTheSupportedEngines_CannotEnforceTenancyInTheDatabase()
    {
        // Stated as a test because it is the evidence for a design decision
        // people keep wanting to revisit. If the tenant predicate were
        // delegated to row-level security, two of four engines would silently
        // enforce nothing.
        IProviderCapabilities[] capabilities =
        [
            new SqlServerCapabilities(),
            new PostgreSqlCapabilities(),
            new SqliteCapabilities(),
            new MySqlCapabilities(),
        ];

        Assert.Equal(2, capabilities.Count(c => !c.SupportsRowLevelSecurity));
    }
}
