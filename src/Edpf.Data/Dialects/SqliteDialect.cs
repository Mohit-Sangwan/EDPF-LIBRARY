using Edpf.Abstractions.Data;

namespace Edpf.Data.Dialects;

/// <summary>
/// SQLite dialect (ADR-037 v1.0 scope).
/// </summary>
/// <remarks>
/// <para>
/// Added as the third dialect for a specific reason: **two engines is a
/// coincidence, three is evidence.** SQL Server and PostgreSQL already
/// disagreed enough to hide a real defect for months (ADR-036), and an
/// abstraction validated against exactly two implementations is an
/// abstraction shaped around two implementations.
/// </para>
/// <para>
/// SQLite is also the one engine that needs no procurement, no container and
/// no credentials, which makes it the honest first choice when the constraint
/// on the other eleven providers is licence lead time rather than code.
/// </para>
/// <para>
/// It is not a toy target. Embedded and edge deployments are real — a clinic
/// with intermittent connectivity, a device host, a desktop client — and those
/// are exactly the deployments where the framework's tenancy and audit rules
/// still have to hold.
/// </para>
/// </remarks>
public sealed class SqliteDialect : SqlDialectBase
{
    /// <summary>The provider name this dialect serves.</summary>
    public const string Provider = "Sqlite";

    /// <inheritdoc />
    public override string ProviderName => Provider;

    /// <inheritdoc />
    protected override char QuoteOpen => '"';

    /// <inheritdoc />
    protected override char QuoteClose => '"';

    /// <summary>
    /// 128 characters — a deliberate portability guard, not an engine limit.
    /// </summary>
    /// <remarks>
    /// SQLite imposes no identifier length limit at all. Declaring that
    /// honestly would mean <c>int.MaxValue</c>, which disables the rejection
    /// check the base class exists to perform. 128 matches the most permissive
    /// Tier A engine, so a schema that passes here is not immediately
    /// unportable. **A schema intended to also run on PostgreSQL must stay
    /// within 63** — this guard does not enforce that and is not a substitute
    /// for testing against the engines you actually target.
    /// </remarks>
    protected override int MaxIdentifierLength => 128;

    /// <inheritdoc />
    public override string PaginationClause(string skipParameter, string takeParameter)
        => $"LIMIT {Parameter(takeParameter)} OFFSET {Parameter(skipParameter)}";

    /// <summary>
    /// <c>RETURNING *</c>, available since SQLite 3.35.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>last_insert_rowid()</c>. That function returns the
    /// rowid of the last insert **on the connection**, so with a connection
    /// pool or any concurrent write on the same connection it can return
    /// another statement's row. <c>RETURNING</c> is scoped to the statement
    /// and cannot.
    /// </remarks>
    public override string IdentityRetrievalClause() => "RETURNING *";

    /// <summary>
    /// UTC to millisecond precision.
    /// </summary>
    /// <remarks>
    /// <c>CURRENT_TIMESTAMP</c> is UTC in SQLite but truncates to whole
    /// seconds, which would collapse ordering for anything written in the same
    /// second. <c>%f</c> gives milliseconds — coarser than SQL Server's 100 ns
    /// and PostgreSQL's microsecond, and therefore the resolution
    /// <see cref="Edpf.Core.Time.StorableInstant"/> would have to fall to if
    /// SQLite ever carries a hashed timestamp (ADR-036).
    /// </remarks>
    public override string UtcNowExpression() => "strftime('%Y-%m-%d %H:%M:%f', 'now')";

    /// <summary>JSON1, compiled into SQLite since 3.38.</summary>
    /// <param name="columnExpression">The column holding JSON.</param>
    /// <param name="jsonPathParameter">The framework-named path parameter.</param>
    /// <returns>The extraction expression.</returns>
    public override string JsonValue(string columnExpression, string jsonPathParameter)
        => $"json_extract({columnExpression}, {Parameter(jsonPathParameter)})";

    // Concat and BooleanLiteral are NOT overridden, and that is worth stating
    // because both look like they should be.
    //
    // The base emits `||`, which is SQLite's concatenation operator — SQL
    // Server is the odd one out here, not SQLite.
    //
    // The base emits TRUE/FALSE. SQLite has no boolean TYPE, but it has
    // recognised the TRUE and FALSE keywords as aliases for 1 and 0 since
    // 3.23, and Microsoft.Data.Sqlite bundles far newer than that. Rewriting
    // these to 1/0 would be a change that looks like a fix and is not one.
}

/// <summary>
/// SQLite capabilities, declared honestly (ADR-016).
/// </summary>
/// <remarks>
/// **Four of these are false, and that is the point of capability
/// negotiation.** A dialect that claimed parity with PostgreSQL would let the
/// framework choose a code path SQLite cannot honour, and the failure would
/// arrive at runtime in a deployment rather than at composition time.
/// </remarks>
public sealed class SqliteCapabilities : IProviderCapabilities
{
    /// <summary>
    /// False. SQLite has neither table-valued parameters nor array
    /// parameters, so set-based paths must fall back to batched statements
    /// bounded by <see cref="MaxParameterCount"/>.
    /// </summary>
    public bool SupportsTableValuedParameters => false;

    /// <summary>True — <c>SAVEPOINT</c> is supported and nests.</summary>
    public bool SupportsSavepoints => true;

    /// <inheritdoc />
    public bool SupportsStreaming => true;

    /// <summary>
    /// False. There is no bulk-copy API; the fast path is a transaction around
    /// prepared inserts, which is a different thing and should not be sold as
    /// the same one.
    /// </summary>
    public bool SupportsBulkCopy => false;

    /// <inheritdoc />
    public bool SupportsKeysetPagination => true;

    /// <summary>True — JSON1 is compiled in by default.</summary>
    public bool SupportsJsonQuery => true;

    /// <summary>
    /// **False, and this is the consequential one.** SQLite has no row-level
    /// security. Any tenant isolation that leaned on the database to enforce
    /// it would silently stop being enforced here.
    /// </summary>
    /// <remarks>
    /// EDPF does not lean on it: the tenant predicate is emitted by the query
    /// compiler, first and unconditionally, on every provider (ADR-004,
    /// Phase 10 §④). RLS is defence in depth where an engine offers it, never
    /// the mechanism. SQLite is the case that proves the difference matters.
    /// </remarks>
    public bool SupportsRowLevelSecurity => false;

    /// <summary>True — <c>INSERT … ON CONFLICT</c> since 3.24.</summary>
    public bool SupportsUpsert => true;

    /// <summary>True — <c>RETURNING</c> since 3.35.</summary>
    public bool SupportsIdentityRetrieval => true;

    /// <summary>
    /// False. SQLite takes a database-wide write lock, and <c>ALTER TABLE</c>
    /// cannot drop or alter a column without rebuilding the table. The
    /// expand–migrate–contract discipline (ADR-021) still applies, but its
    /// "without blocking readers and writers" premise does not hold here.
    /// </summary>
    public bool SupportsZeroDowntimeDdl => false;

    /// <summary>
    /// 32,766 — <c>SQLITE_MAX_VARIABLE_NUMBER</c> since 3.32.
    /// </summary>
    /// <remarks>
    /// It was 999 before 3.32, and a deployment running an older bundled
    /// SQLite will fail on a batch this framework considers legal. That is a
    /// deployment-verification item, not something the dialect can detect.
    /// </remarks>
    public int MaxParameterCount => 32766;

    /// <summary>
    /// 500 — smaller than the Tier A engines, because without bulk copy every
    /// row in a batch consumes parameters from the budget above.
    /// </summary>
    public int MaxBatchSize => 500;

    /// <inheritdoc />
    public int MaxIdentifierLength => 128;
}
