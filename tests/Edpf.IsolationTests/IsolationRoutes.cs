namespace Edpf.IsolationTests;

/// <summary>
/// The twelve cross-tenant routes enumerated in Phase 12 §④. Every route has
/// a test class in this assembly, and every phase that introduces a new data
/// path adds to this list.
/// </summary>
/// <remarks>
/// The list is code rather than prose so that "the isolation suite covers
/// every route" is checkable: <c>IsolationCoverageTests</c> asserts that each
/// entry has a corresponding test class.
/// </remarks>
public static class IsolationRoutes
{
    /// <summary>Repository and query paths (Phase 10).</summary>
    public const string Repository = "Repository";

    /// <summary>The audited raw-SQL escape hatch (Phase 08).</summary>
    public const string RawSql = "RawSql";

    /// <summary>Cache key collision (Phase 15).</summary>
    public const string CacheKey = "CacheKey";

    /// <summary>Search index and aggregations (Phase 16).</summary>
    public const string SearchIndex = "SearchIndex";

    /// <summary>Message routing and consumption (Phase 26).</summary>
    public const string MessageRouting = "MessageRouting";

    /// <summary>Blob paths (Phase 14).</summary>
    public const string BlobPath = "BlobPath";

    /// <summary>Log and correlation context (Phase 05).</summary>
    public const string LogCorrelation = "LogCorrelation";

    /// <summary>Error messages used as an enumeration oracle (Phase 18).</summary>
    public const string ErrorEnumeration = "ErrorEnumeration";

    /// <summary>Timing side-channels distinguishing "absent" from "another tenant's".</summary>
    public const string TimingSideChannel = "TimingSideChannel";

    /// <summary>Connection reuse across tenant scopes (Phase 07).</summary>
    public const string ConnectionReuse = "ConnectionReuse";

    /// <summary>Background job ambient context (Phase 25).</summary>
    public const string BackgroundJobContext = "BackgroundJobContext";

    /// <summary>Outbox dispatch carrying another tenant's payload (Phase 09).</summary>
    public const string OutboxDispatch = "OutboxDispatch";

    /// <summary>Every route the suite must cover.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Repository,
        RawSql,
        CacheKey,
        SearchIndex,
        MessageRouting,
        BlobPath,
        LogCorrelation,
        ErrorEnumeration,
        TimingSideChannel,
        ConnectionReuse,
        BackgroundJobContext,
        OutboxDispatch,
    ];
}

/// <summary>
/// Marks a test class as covering one isolation route, so coverage of the
/// route list is machine-checkable rather than asserted in a document.
/// </summary>
/// <param name="route">The route from <see cref="IsolationRoutes"/>.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class CoversIsolationRouteAttribute(string route) : Attribute
{
    /// <summary>The covered route.</summary>
    public string Route { get; } = route;
}
