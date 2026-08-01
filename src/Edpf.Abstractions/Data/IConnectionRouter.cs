using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Tenancy;

namespace Edpf.Abstractions.Data;

/// <summary>Whether an operation needs the primary or may use a replica.</summary>
public enum ReadWriteIntent
{
    /// <summary>A write. Always routed to the primary.</summary>
    Write = 0,

    /// <summary>
    /// A read that tolerates replica lag. Routed to a replica by policy —
    /// unless the caller's session has written recently, in which case
    /// read-your-own-writes forces the primary (ADR-017).
    /// </summary>
    Read = 1,

    /// <summary>A read that must observe the latest committed state. Always the primary.</summary>
    ReadConsistent = 2,
}

/// <summary>
/// Chooses the endpoint for an operation (ADR-017). Routing carries three
/// concerns that must not be separated: read/write intent, session
/// consistency, and data residency.
/// </summary>
public interface IConnectionRouter
{
    /// <summary>
    /// Resolves the endpoint for an operation.
    /// </summary>
    /// <param name="intent">Whether the operation writes, or may read stale.</param>
    /// <param name="tenant">The tenant, carrying its pinned region (ADR-010).</param>
    /// <param name="sessionToken">
    /// The caller's session-consistency token from its last write, or null.
    /// Within the staleness window this forces the primary, because silent
    /// replica lag is a correctness bug that surfaces as a support ticket
    /// months later.
    /// </param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <returns>
    /// The chosen endpoint, or failure with
    /// <see cref="ErrorCodes.ResidencyViolation"/> when the only candidates
    /// are outside the tenant's region.
    /// </returns>
    Task<Result<DatabaseEndpoint>> RouteAsync(
        ReadWriteIntent intent,
        ITenantContext tenant,
        SessionConsistencyToken? sessionToken,
        CancellationToken cancellationToken);
}

/// <summary>One reachable database endpoint.</summary>
public sealed class DatabaseEndpoint
{
    /// <summary>
    /// Initializes an endpoint.
    /// </summary>
    /// <param name="name">Stable endpoint name for diagnostics. Never a connection string.</param>
    /// <param name="region">The region this endpoint sits in (ADR-010).</param>
    /// <param name="isPrimary">True when this endpoint accepts writes.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public DatabaseEndpoint(string name, string region, bool isPrimary)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Region = region ?? throw new ArgumentNullException(nameof(region));
        IsPrimary = isPrimary;
    }

    /// <summary>Endpoint name. Safe to log — it is never a connection string.</summary>
    public string Name { get; }

    /// <summary>The endpoint's region.</summary>
    public string Region { get; }

    /// <summary>True when this endpoint accepts writes.</summary>
    public bool IsPrimary { get; }
}

/// <summary>
/// Proof that a session wrote at a point in time, so subsequent reads can be
/// held to the primary until replicas are known to have caught up
/// (read-your-own-writes, ADR-017).
/// </summary>
public sealed class SessionConsistencyToken
{
    /// <summary>
    /// Initializes a token.
    /// </summary>
    /// <param name="sessionId">The writing session.</param>
    /// <param name="writtenUtc">When the write committed.</param>
    /// <param name="stalenessWindow">How long reads stay pinned to the primary.</param>
    public SessionConsistencyToken(string sessionId, DateTimeOffset writtenUtc, TimeSpan stalenessWindow)
    {
        SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        WrittenUtc = writtenUtc;
        StalenessWindow = stalenessWindow;
    }

    /// <summary>The session that wrote.</summary>
    public string SessionId { get; }

    /// <summary>When the write committed.</summary>
    public DateTimeOffset WrittenUtc { get; }

    /// <summary>How long after the write reads remain pinned to the primary.</summary>
    public TimeSpan StalenessWindow { get; }

    /// <summary>
    /// True when the window is still open and reads must use the primary.
    /// </summary>
    /// <param name="now">The current instant, from <see cref="IClock"/>.</param>
    public bool RequiresPrimary(DateTimeOffset now) => now - WrittenUtc < StalenessWindow;
}

/// <summary>Chooses among healthy replicas (ADR-017).</summary>
public interface IReplicaSelector
{
    /// <summary>The selection policy this implementation applies.</summary>
    string PolicyName { get; }

    /// <summary>
    /// Selects a replica from the healthy candidates.
    /// </summary>
    /// <param name="candidates">Healthy replicas in the required region.</param>
    /// <returns>
    /// The chosen replica, or null when none is available — the caller then
    /// falls back to the primary rather than failing the read.
    /// </returns>
    DatabaseEndpoint? Select(IReadOnlyList<DatabaseEndpoint> candidates);
}
