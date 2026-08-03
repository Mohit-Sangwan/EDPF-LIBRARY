using Edpf.Abstractions.Data;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;
using Edpf.Abstractions.Tenancy;
using Edpf.Data.Dialects;
using Edpf.Data.Query;

namespace Edpf.UnitTests.Data;

/// <summary>
/// The third dialect (ADR-037 v1.0 scope) — two engines is a coincidence.
/// </summary>
public sealed class SqliteDialectTests
{
    private static readonly SqliteDialect Dialect = new();

    private static ITenantContext Tenant => new TenantDescriptor(
        Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
        "tenant-a", "eu-west", TenantIsolationMode.SharedSchema, Guid.NewGuid());

    // ── the properties every dialect must hold ────────────────────────────

    [Fact]
    public void IllegalIdentifier_IsRejectedNotEscaped()
    {
        // The ADR-018 property, restated on a new dialect. If a third
        // implementation could be talked into escaping, the guarantee was a
        // property of two implementations rather than of the design.
        Assert.Throws<ArgumentException>(() => Dialect.QuoteIdentifier("Patients\"; DROP TABLE X--"));
        Assert.Throws<ArgumentException>(() => Dialect.QuoteIdentifier("  "));
    }

    [Fact]
    public void LegalIdentifier_IsDoubleQuoted()
    {
        Assert.Equal("\"MedicalRecordNumber\"", Dialect.QuoteIdentifier("MedicalRecordNumber"));
    }

    [Fact]
    public void OverlongIdentifier_IsRejected()
    {
        // SQLite has no engine limit; this guard is a deliberate portability
        // choice and is documented as one.
        Assert.Throws<ArgumentException>(() => Dialect.QuoteIdentifier(new string('a', 129)));
        Assert.Equal($"\"{new string('a', 128)}\"", Dialect.QuoteIdentifier(new string('a', 128)));
    }

    [Fact]
    public void TenantPredicate_IsStillEmittedFirstAndUnconditionally()
    {
        // The isolation guarantee must not be a property of the two engines it
        // was developed against. SQLite has NO row-level security, so if the
        // framework had ever leaned on the database to enforce tenancy, this
        // is where it would stop being enforced.
        var compiler = new QueryCompiler(Dialect, TestEntities.SubjectRecord());

        CompiledQuery query = compiler.CompilePaged(
            Specification<object>.Create(), Tenant, new PageRequest(1, 10)).Value;

        int tenantIndex = query.Sql.IndexOf("TenantId", StringComparison.Ordinal);
        Assert.True(tenantIndex > 0, "The tenant predicate is absent.");
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

    // ── where SQLite legitimately differs ─────────────────────────────────

    [Fact]
    public void Pagination_UsesLimitOffset_LikePostgresNotSqlServer()
    {
        Assert.Equal("LIMIT @take OFFSET @skip", Dialect.PaginationClause("skip", "take"));
    }

    [Fact]
    public void IdentityRetrieval_UsesReturning_NotLastInsertRowid()
    {
        // last_insert_rowid() is connection-scoped, so with a pool or any
        // concurrent write on the same connection it can return another
        // statement's row.
        Assert.Equal("RETURNING *", Dialect.IdentityRetrievalClause());
    }

    [Fact]
    public void UtcNow_CarriesSubSecondPrecision()
    {
        // CURRENT_TIMESTAMP is UTC in SQLite but truncates to whole seconds,
        // which would collapse ordering within a second.
        Assert.Contains("%f", Dialect.UtcNowExpression(), StringComparison.Ordinal);
        Assert.Contains("'now'", Dialect.UtcNowExpression(), StringComparison.Ordinal);
    }

    [Fact]
    public void JsonValue_UsesJson1()
    {
        Assert.Equal(
            "json_extract(\"Payload\", @path)",
            Dialect.JsonValue("\"Payload\"", "path"));
    }

    [Fact]
    public void Concat_UsesTheStandardOperator_AndIsNotOverridden()
    {
        // SQL Server is the odd one out here, not SQLite. Recorded so nobody
        // "fixes" this into a CONCAT() call.
        Assert.Equal("\"a\" || \"b\"", Dialect.Concat(["\"a\"", "\"b\""]));
    }

    [Fact]
    public void BooleanLiteral_UsesKeywords_WhichSqliteHasSupportedSince323()
    {
        // Looks like it should be 1/0. It should not — rewriting it would be
        // a change that looks like a fix and is not one.
        Assert.Equal("TRUE", Dialect.BooleanLiteral(true));
        Assert.Equal("FALSE", Dialect.BooleanLiteral(false));
    }

    // ── capabilities declared honestly (ADR-016) ──────────────────────────

    [Fact]
    public void Capabilities_DeclareWhatSqliteCannotDo()
    {
        var capabilities = new SqliteCapabilities();

        // The consequential one: no RLS. EDPF never leaned on it, and this
        // dialect is the case that proves the difference matters.
        Assert.False(capabilities.SupportsRowLevelSecurity);

        Assert.False(capabilities.SupportsTableValuedParameters);
        Assert.False(capabilities.SupportsBulkCopy);
        Assert.False(capabilities.SupportsZeroDowntimeDdl);
    }

    [Fact]
    public void Capabilities_DeclareWhatSqliteCanDo()
    {
        var capabilities = new SqliteCapabilities();

        Assert.True(capabilities.SupportsSavepoints);
        Assert.True(capabilities.SupportsUpsert);
        Assert.True(capabilities.SupportsIdentityRetrieval);
        Assert.True(capabilities.SupportsJsonQuery);
        Assert.True(capabilities.SupportsKeysetPagination);
    }

    [Fact]
    public void ParameterBudget_IsSmallerThanTheTierAEngines()
    {
        // Without bulk copy, every row in a batch spends from this budget,
        // which is why MaxBatchSize is lower too.
        var sqlite = new SqliteCapabilities();
        var postgres = new PostgreSqlCapabilities();

        Assert.True(sqlite.MaxParameterCount < postgres.MaxParameterCount);
        Assert.True(sqlite.MaxBatchSize < postgres.MaxBatchSize);
    }

    // ── the abstraction itself ────────────────────────────────────────────

    [Fact]
    public void ThreeDialects_AgreeOnStructureAndDifferOnlyWhereDeclared()
    {
        // The reason for adding a third: an abstraction validated against two
        // implementations is an abstraction shaped around two implementations.
        SqlDialectBase[] dialects = [new SqlServerDialect(), new PostgreSqlDialect(), new SqliteDialect()];

        foreach (SqlDialectBase dialect in dialects)
        {
            var compiler = new QueryCompiler(dialect, TestEntities.SubjectRecord());

            CompiledQuery query = compiler.CompilePaged(
                Specification<object>.Create().Where("GivenName", FilterOperator.Equal, "x"),
                Tenant,
                new PageRequest(1, 10)).Value;

            // Same shape everywhere: tenant bound, value parameterised,
            // deterministic sort appended.
            Assert.Contains("SELECT", query.Sql, StringComparison.Ordinal);
            Assert.Contains("ORDER BY", query.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain("'x'", query.Sql, StringComparison.Ordinal);
            Assert.True(query.Parameters.ContainsKey("tenantId"), dialect.ProviderName);
        }
    }

    [Fact]
    public void EveryDialect_HasADistinctProviderName()
    {
        // Capability negotiation resolves on this; a collision would silently
        // hand one engine another's capabilities.
        string[] names =
        [
            new SqlServerDialect().ProviderName,
            new PostgreSqlDialect().ProviderName,
            new SqliteDialect().ProviderName,
        ];

        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
