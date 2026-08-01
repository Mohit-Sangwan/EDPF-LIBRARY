using Edpf.Abstractions.Data;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;
using Edpf.Metadata;
using Edpf.Abstractions.Tenancy;
using Edpf.Data.Dialects;
using Edpf.Data.Query;

namespace Edpf.UnitTests.Data;

/// <summary>
/// The dialect half of the Phase 06 conformance suite: identifier handling,
/// pagination syntax, and keyset correctness, asserted identically for both
/// Tier A providers.
/// </summary>
public sealed class DialectTests
{
    public static TheoryData<SqlDialectBase> AllDialects =>
        new(new SqlServerDialect(), new PostgreSqlDialect());

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void QuoteIdentifier_LegalName_IsQuoted(SqlDialectBase dialect)
    {
        string quoted = dialect.QuoteIdentifier("SubjectRecord");

        Assert.NotEqual("SubjectRecord", quoted);
        Assert.Contains("SubjectRecord", quoted, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void QuoteIdentifier_SchemaQualified_QuotesEachPart(SqlDialectBase dialect)
    {
        string quoted = dialect.QuoteIdentifier("dbo.PATIENT");

        // Each part is quoted separately, so a dot cannot smuggle structure
        // past the quoting.
        Assert.Contains('.', quoted);
        Assert.Contains("dbo", quoted, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void QuoteIdentifier_IllegalCharacter_IsRejectedNotEscaped(SqlDialectBase dialect)
    {
        // Rejecting beats escaping: an identifier needing escape did not come
        // from metadata, which means something upstream is wrong.
        string[] hostile =
        [
            "PATIENT; DROP TABLE X",
            "PATIENT]--",
            "PATIENT\"",
            "PATIENT'",
            "PATIENT WITH SPACE",
            "PATIENT\nNEWLINE",
        ];

        foreach (string name in hostile)
        {
            Assert.Throws<ArgumentException>(() => dialect.QuoteIdentifier(name));
        }
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void QuoteIdentifier_Blank_IsRejected(SqlDialectBase dialect)
    {
        Assert.Throws<ArgumentException>(() => dialect.QuoteIdentifier("  "));
    }

    [Fact]
    public void QuoteIdentifier_OverEngineLimit_IsRejected()
    {
        // PostgreSQL truncates at 63 silently; EDPF refuses, because a
        // truncated identifier is a different object.
        var dialect = new PostgreSqlDialect();

        Assert.Throws<ArgumentException>(() => dialect.QuoteIdentifier(new string('a', 64)));
        _ = dialect.QuoteIdentifier(new string('a', 63));
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void Parameter_NonAlphanumericName_IsRejected(SqlDialectBase dialect)
    {
        Assert.Throws<ArgumentException>(() => dialect.Parameter("p0; DROP TABLE X"));
    }

    [Fact]
    public void PaginationClause_DiffersPerEngineButBothParameterise()
    {
        string sqlServer = new SqlServerDialect().PaginationClause("skip", "take");
        string postgres = new PostgreSqlDialect().PaginationClause("skip", "take");

        Assert.Equal("OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY", sqlServer);
        Assert.Equal("LIMIT @take OFFSET @skip", postgres);
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void KeysetPredicate_SingleColumn_IsSimpleComparison(SqlDialectBase dialect)
    {
        string predicate = dialect.KeysetPredicate([new SortColumn("Id")], ["c0"]);

        Assert.Contains("> @c0", predicate, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void KeysetPredicate_MultipleColumns_ExpandsLexicographically(SqlDialectBase dialect)
    {
        string predicate = dialect.KeysetPredicate(
            [new SortColumn("FamilyName"), new SortColumn("Id")],
            ["c0", "c1"]);

        // (family > @c0) OR (family = @c0 AND id > @c1)
        Assert.Contains(" OR ", predicate, StringComparison.Ordinal);
        Assert.Contains(" AND ", predicate, StringComparison.Ordinal);
        Assert.Contains("= @c0", predicate, StringComparison.Ordinal);
        Assert.Contains("> @c1", predicate, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void KeysetPredicate_DescendingColumn_UsesLessThan(SqlDialectBase dialect)
    {
        string predicate = dialect.KeysetPredicate([new SortColumn("CreatedUtc", descending: true)], ["c0"]);

        Assert.Contains("< @c0", predicate, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void KeysetPredicate_MismatchedCursorLength_IsRejected(SqlDialectBase dialect)
    {
        Assert.Throws<ArgumentException>(
            () => dialect.KeysetPredicate([new SortColumn("Id")], ["c0", "c1"]));
    }

    [Fact]
    public void Capabilities_AreDeclaredHonestly()
    {
        var sqlServer = new SqlServerCapabilities();
        var postgres = new PostgreSqlCapabilities();

        // Both Tier A providers must support what the framework's core paths
        // require, or they could not be Tier A.
        foreach (IProviderCapabilities caps in new IProviderCapabilities[] { sqlServer, postgres })
        {
            Assert.True(caps.SupportsSavepoints);
            Assert.True(caps.SupportsStreaming);
            Assert.True(caps.SupportsKeysetPagination);
            Assert.True(caps.MaxParameterCount > 0);
            Assert.True(caps.MaxIdentifierLength > 0);
        }

        // And they must differ where the engines genuinely differ, rather
        // than converging on a comfortable lie.
        Assert.NotEqual(sqlServer.MaxParameterCount, postgres.MaxParameterCount);
        Assert.NotEqual(sqlServer.MaxIdentifierLength, postgres.MaxIdentifierLength);
        Assert.False(sqlServer.SupportsZeroDowntimeDdl);
        Assert.True(postgres.SupportsZeroDowntimeDdl);
    }
}

/// <summary>Query-shape tests independent of any live database.</summary>
public sealed class QueryCompilerTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private static TenantDescriptor Context => new(
        Tenant, "t", "in-south-1", TenantIsolationMode.SharedSchema, Guid.NewGuid());

    private static QueryCompiler Compiler(SqlDialectBase dialect) => new(dialect, TestEntities.SubjectRecord());

    [Fact]
    public void CompilePaged_Always_AppendsAStableTiebreaker()
    {
        // BRL-017: without a stable total order, rows can appear on two pages
        // or none, and it surfaces as "a record went missing".
        Result<CompiledQuery> result = Compiler(new SqlServerDialect()).CompilePaged(
            Specification<object>.Create().OrderBy("FamilyName"),
            Context,
            new PageRequest(1, 10));

        Assert.EndsWith("[Id] ASC OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY",
            result.Value.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CompilePaged_SortAlreadyIncludesId_DoesNotDuplicateIt()
    {
        Result<CompiledQuery> result = Compiler(new SqlServerDialect()).CompilePaged(
            Specification<object>.Create().OrderBy("Id", descending: true),
            Context,
            new PageRequest(1, 10));

        int occurrences = result.Value.Sql.Split("[Id]").Length - 1;
        Assert.Equal(1, occurrences - CountIdInProjection(result.Value.Sql));
    }

    private static int CountIdInProjection(string sql)
    {
        int fromIndex = sql.IndexOf(" FROM ", StringComparison.Ordinal);
        return sql[..fromIndex].Split("[Id]").Length - 1;
    }

    [Fact]
    public void CompilePaged_SecondPage_SkipsCorrectly()
    {
        Result<CompiledQuery> result = Compiler(new PostgreSqlDialect()).CompilePaged(
            Specification<object>.Create(), Context, new PageRequest(3, 20));

        Assert.Equal(40, result.Value.Parameters["skip"]);
        Assert.Equal(20, result.Value.Parameters["take"]);
    }

    [Fact]
    public void CompileKeyset_FirstPage_HasNoCursorPredicate()
    {
        Result<CompiledQuery> result = Compiler(new SqlServerDialect()).CompileKeyset(
            Specification<object>.Create().OrderBy("FamilyName"),
            Context,
            cursorValues: [],
            pageSize: 10);

        Assert.DoesNotContain("cursor", result.Value.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CompileKeyset_CursorOfWrongShape_IsRejected()
    {
        // A cursor from a differently-sorted query would silently skip or
        // repeat rows; it is refused instead.
        Result<CompiledQuery> result = Compiler(new SqlServerDialect()).CompileKeyset(
            Specification<object>.Create().OrderBy("FamilyName"),
            Context,
            cursorValues: ["only-one-value"],
            pageSize: 10);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.ValidationFailed, result.Error!.Code);
    }

    [Fact]
    public void CompileKeyset_PageSizeOverMaximum_IsRejected()
    {
        Result<CompiledQuery> result = Compiler(new SqlServerDialect()).CompileKeyset(
            Specification<object>.Create().OrderBy("FamilyName"),
            Context,
            cursorValues: [],
            pageSize: PageRequest.MaxPageSize + 1);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void CompilePaged_DefaultProjection_ExcludesNonProjectableFields()
    {
        Result<CompiledQuery> result = Compiler(new SqlServerDialect()).CompilePaged(
            Specification<object>.Create(), Context, new PageRequest(1, 10));

        Assert.DoesNotContain("InternalRiskScore", result.Value.Sql, StringComparison.Ordinal);
        Assert.Contains("[GivenName]", result.Value.Sql, StringComparison.Ordinal);
    }
}
