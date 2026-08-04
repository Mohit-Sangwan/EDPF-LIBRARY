using System;
using System.Collections.Generic;
using Edpf.Abstractions.Data;

namespace Edpf.Data.Dialects;

/// <summary>
/// MySQL dialect (ADR-037 v1.0 scope), completing the fourth engine.
/// </summary>
/// <remarks>
/// <para>
/// The interesting engine of the four, because it is the one that disagrees
/// with the others in ways that are silent rather than loud. SQLite lacks
/// features and says so; MySQL has features that mean something different.
/// </para>
/// <para>
/// Two of those differences are recorded as overrides below, and both would
/// have produced working software that was wrong.
/// </para>
/// </remarks>
public sealed class MySqlDialect : SqlDialectBase
{
    /// <summary>The provider name this dialect serves.</summary>
    public const string Provider = "MySql";

    /// <inheritdoc />
    public override string ProviderName => Provider;

    /// <summary>Backtick, not the double quote the other three use.</summary>
    /// <remarks>
    /// MySQL only treats <c>"</c> as an identifier quote under
    /// <c>ANSI_QUOTES</c>, which is not the default and is not something a
    /// library may assume about someone else's server. A backtick works
    /// regardless of <c>sql_mode</c>, and depending on a server setting for a
    /// *security* property — quoting is what keeps an identifier an identifier
    /// — is not a trade worth making for aesthetics.
    /// </remarks>
    protected override char QuoteOpen => '`';

    /// <inheritdoc />
    protected override char QuoteClose => '`';

    /// <summary>64 characters, the documented limit for most object names.</summary>
    protected override int MaxIdentifierLength => 64;

    /// <inheritdoc />
    public override string PaginationClause(string skipParameter, string takeParameter)
        => $"LIMIT {Parameter(takeParameter)} OFFSET {Parameter(skipParameter)}";

    /// <summary>
    /// Not supported. MySQL has no <c>RETURNING</c>, and this throws rather
    /// than quietly returning <c>LAST_INSERT_ID()</c>.
    /// </summary>
    /// <returns>Never returns.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    /// <remarks>
    /// <para>
    /// <c>LAST_INSERT_ID()</c> is scoped to the connection, so under a pool or
    /// any concurrent write on the same connection it can return another
    /// statement's id. It is right almost all of the time, which is the worst
    /// possible property for a correctness primitive: the failure is rare,
    /// load-dependent, and produces a valid-looking id pointing at somebody
    /// else's row.
    /// </para>
    /// <para>
    /// Returning it here would make <see cref="MySqlCapabilities.SupportsIdentityRetrieval"/>
    /// a comment. Throwing makes the capability load-bearing: code that
    /// negotiated correctly never reaches this, and code that did not fails at
    /// the seam instead of in production six months later.
    /// </para>
    /// </remarks>
    public override string IdentityRetrievalClause()
        => throw new NotSupportedException(
            "MySQL cannot retrieve an inserted identity in the same statement. "
            + "Check IProviderCapabilities.SupportsIdentityRetrieval and select the "
            + "explicit-key path; LAST_INSERT_ID() is connection-scoped and is not a substitute.");

    /// <summary>
    /// UTC to microsecond precision.
    /// </summary>
    /// <remarks>
    /// The argument to <c>UTC_TIMESTAMP</c> is not optional in practice.
    /// Without it MySQL truncates to whole seconds, and an audit chain that
    /// hashes a timestamp it did not store is precisely the defect ADR-036
    /// records. Microseconds also match PostgreSQL exactly, so
    /// <c>StorableInstant</c> needs no third resolution.
    /// </remarks>
    public override string UtcNowExpression() => "UTC_TIMESTAMP(6)";

    /// <summary>
    /// <c>CONCAT()</c>, because <c>||</c> means something else here.
    /// </summary>
    /// <param name="expressions">The expressions to join.</param>
    /// <returns>The concatenation expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expressions"/> is null.</exception>
    /// <exception cref="ArgumentException">No expressions were supplied.</exception>
    /// <remarks>
    /// **This override is not stylistic.** By default MySQL parses <c>||</c> as
    /// logical OR, so the base implementation would not fail — it would return
    /// <c>0</c> or <c>1</c> where a string was expected, and the column would
    /// fill with plausible-looking rubbish. Only <c>PIPES_AS_CONCAT</c> in
    /// <c>sql_mode</c> changes that, and a library does not get to assume
    /// another organisation's server configuration.
    /// </remarks>
    public override string Concat(IReadOnlyList<string> expressions)
    {
        if (expressions is null)
        {
            throw new ArgumentNullException(nameof(expressions));
        }

        if (expressions.Count == 0)
        {
            throw new ArgumentException("Concat requires at least one expression.", nameof(expressions));
        }

        return "CONCAT(" + string.Join(", ", expressions) + ")";
    }

    /// <summary>JSON functions, available since MySQL 5.7.</summary>
    /// <param name="columnExpression">The column holding JSON.</param>
    /// <param name="jsonPathParameter">The framework-named path parameter.</param>
    /// <returns>The extraction expression.</returns>
    public override string JsonValue(string columnExpression, string jsonPathParameter)
        => $"JSON_EXTRACT({columnExpression}, {Parameter(jsonPathParameter)})";
}

/// <summary>MySQL capabilities, declared honestly (ADR-016).</summary>
public sealed class MySqlCapabilities : IProviderCapabilities
{
    /// <summary>
    /// False. MySQL has neither table-valued parameters nor array parameters;
    /// the usual workaround is a temporary table, which is a different
    /// mechanism with different transaction semantics and should not be
    /// declared as the same capability.
    /// </summary>
    public bool SupportsTableValuedParameters => false;

    /// <inheritdoc />
    public bool SupportsSavepoints => true;

    /// <inheritdoc />
    public bool SupportsStreaming => true;

    /// <summary><c>LOAD DATA INFILE</c> and the bulk-loader protocol.</summary>
    public bool SupportsBulkCopy => true;

    /// <inheritdoc />
    public bool SupportsKeysetPagination => true;

    /// <summary>The native <c>JSON</c> type and its functions.</summary>
    public bool SupportsJsonQuery => true;

    /// <summary>
    /// False. MySQL has no row-level security; the nearest equivalent is
    /// definer-rights views, which is an access-control pattern rather than the
    /// feature.
    /// </summary>
    /// <remarks>
    /// Two of the four supported engines therefore lack RLS. That is the
    /// clearest possible argument for the tenant predicate being emitted by the
    /// query compiler rather than delegated to the database: half the estate
    /// could not enforce it if the framework asked.
    /// </remarks>
    public bool SupportsRowLevelSecurity => false;

    /// <summary><c>INSERT … ON DUPLICATE KEY UPDATE</c>.</summary>
    public bool SupportsUpsert => true;

    /// <summary>
    /// False — no <c>RETURNING</c>, and the connection-scoped alternative does
    /// not qualify. See <see cref="MySqlDialect.IdentityRetrievalClause"/>.
    /// </summary>
    public bool SupportsIdentityRetrieval => false;

    /// <summary>
    /// True for MySQL 8's instant DDL, with a real caveat: the set of
    /// operations that qualify is narrower than PostgreSQL's, and one that does
    /// not qualify rebuilds the table under a metadata lock.
    /// </summary>
    /// <remarks>
    /// Declared true because expand–migrate–contract's actual requirement —
    /// adding a nullable column without blocking — is instant on MySQL 8. The
    /// caveat belongs in the migration runbook, not in a flag that would then
    /// mean "no" on an engine where the common case is yes.
    /// </remarks>
    public bool SupportsZeroDowntimeDdl => true;

    /// <summary>
    /// 65,535 — the protocol's placeholder limit, matching PostgreSQL by
    /// coincidence of both using a 16-bit count.
    /// </summary>
    public int MaxParameterCount => 65535;

    /// <inheritdoc />
    public int MaxBatchSize => 1000;

    /// <inheritdoc />
    public int MaxIdentifierLength => 64;
}
