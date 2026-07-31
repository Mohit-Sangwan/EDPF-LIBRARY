using System;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Consistency;

/// <summary>
/// Idempotency-key bookkeeping (§10.1 Consistency; ADR-003, ADR-012 stage 6).
/// A retried request with the same key and payload replays the stored
/// response; the same key with a different payload is a conflict
/// (<see cref="ErrorCodes.IdempotencyConflict"/>).
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Looks up a previously completed request.
    /// </summary>
    /// <param name="tenantId">The tenant scope of the key.</param>
    /// <param name="idempotencyKey">The caller-supplied key.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The stored record, or null when the key is unseen (a miss is a normal outcome, not a failure).</returns>
    Task<IdempotencyRecord?> FindAsync(Guid tenantId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the outcome of a completed request.
    /// </summary>
    /// <param name="record">The completed request record.</param>
    /// <param name="cancellationToken">Cancels before the write.</param>
    /// <returns>Success, or <see cref="ErrorCodes.IdempotencyConflict"/> on concurrent duplicate.</returns>
    Task<Result> SaveAsync(IdempotencyRecord record, CancellationToken cancellationToken);
}

/// <summary>A completed request remembered for replay.</summary>
public sealed class IdempotencyRecord
{
    /// <summary>
    /// Initializes a record.
    /// </summary>
    /// <param name="tenantId">The tenant scope.</param>
    /// <param name="idempotencyKey">The caller-supplied key.</param>
    /// <param name="requestHash">SHA-256 of the canonical request payload, base64.</param>
    /// <param name="responseStatusCode">The HTTP status originally returned.</param>
    /// <param name="responseBody">The response body originally returned. Carries no more than the original response did.</param>
    /// <param name="createdUtc">When the original request completed.</param>
    /// <exception cref="ArgumentNullException">Any string argument is null.</exception>
    public IdempotencyRecord(
        Guid tenantId,
        string idempotencyKey,
        string requestHash,
        int responseStatusCode,
        string responseBody,
        DateTimeOffset createdUtc)
    {
        TenantId = tenantId;
        IdempotencyKey = idempotencyKey ?? throw new ArgumentNullException(nameof(idempotencyKey));
        RequestHash = requestHash ?? throw new ArgumentNullException(nameof(requestHash));
        ResponseStatusCode = responseStatusCode;
        ResponseBody = responseBody ?? throw new ArgumentNullException(nameof(responseBody));
        CreatedUtc = createdUtc;
    }

    /// <summary>The tenant scope of the key.</summary>
    public Guid TenantId { get; }

    /// <summary>The caller-supplied key.</summary>
    public string IdempotencyKey { get; }

    /// <summary>SHA-256 of the canonical request payload, base64 — detects key reuse with a different payload.</summary>
    public string RequestHash { get; }

    /// <summary>The HTTP status originally returned.</summary>
    public int ResponseStatusCode { get; }

    /// <summary>The response body originally returned.</summary>
    public string ResponseBody { get; }

    /// <summary>When the original request completed (UTC).</summary>
    public DateTimeOffset CreatedUtc { get; }
}
