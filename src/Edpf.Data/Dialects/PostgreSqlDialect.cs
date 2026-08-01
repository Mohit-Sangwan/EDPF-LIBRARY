using Edpf.Abstractions.Data;

namespace Edpf.Data.Dialects;

/// <summary>PostgreSQL dialect — Tier A reference implementation (ADR-008).</summary>
public sealed class PostgreSqlDialect : SqlDialectBase
{
    /// <summary>The provider name this dialect serves.</summary>
    public const string Provider = "PostgreSql";

    /// <inheritdoc />
    public override string ProviderName => Provider;

    /// <inheritdoc />
    protected override char QuoteOpen => '"';

    /// <inheritdoc />
    protected override char QuoteClose => '"';

    /// <inheritdoc />
    protected override int MaxIdentifierLength => 63;

    /// <inheritdoc />
    public override string PaginationClause(string skipParameter, string takeParameter)
        => $"LIMIT {Parameter(takeParameter)} OFFSET {Parameter(skipParameter)}";

    /// <inheritdoc />
    public override string IdentityRetrievalClause() => "RETURNING *";

    /// <inheritdoc />
    public override string UtcNowExpression() => "(NOW() AT TIME ZONE 'utc')";

    /// <inheritdoc />
    public override string JsonValue(string columnExpression, string jsonPathParameter)
        => $"jsonb_path_query_first({columnExpression}, {Parameter(jsonPathParameter)}::jsonpath)";
}

/// <summary>PostgreSQL capabilities, declared honestly (ADR-016).</summary>
public sealed class PostgreSqlCapabilities : IProviderCapabilities
{
    /// <summary>
    /// PostgreSQL has no TVP, but array parameters serve the same purpose and
    /// the framework's set-based paths use them, so the capability is true.
    /// </summary>
    public bool SupportsTableValuedParameters => true;

    /// <inheritdoc />
    public bool SupportsSavepoints => true;

    /// <inheritdoc />
    public bool SupportsStreaming => true;

    /// <summary><c>COPY</c>.</summary>
    public bool SupportsBulkCopy => true;

    /// <inheritdoc />
    public bool SupportsKeysetPagination => true;

    /// <summary><c>jsonb</c> path queries.</summary>
    public bool SupportsJsonQuery => true;

    /// <inheritdoc />
    public bool SupportsRowLevelSecurity => true;

    /// <summary><c>INSERT … ON CONFLICT</c>.</summary>
    public bool SupportsUpsert => true;

    /// <summary><c>RETURNING</c>.</summary>
    public bool SupportsIdentityRetrieval => true;

    /// <summary>
    /// True: PostgreSQL adds nullable columns and creates indexes
    /// concurrently without blocking readers or writers, which is the case
    /// expand–migrate–contract actually needs.
    /// </summary>
    public bool SupportsZeroDowntimeDdl => true;

    /// <summary>The wire protocol's 16-bit parameter count.</summary>
    public int MaxParameterCount => 65535;

    /// <inheritdoc />
    public int MaxBatchSize => 1000;

    /// <inheritdoc />
    public int MaxIdentifierLength => 63;
}
