using System;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Core.Correlation;

/// <summary>
/// Immutable correlation identifiers for one logical operation (Phase 01
/// shared kernel). Created at the pipeline edge (ADR-012 stage 1); flowed to
/// every log entry, trace, audit record and error response.
/// </summary>
public sealed class CorrelationContext : ICorrelationContext
{
    /// <summary>
    /// Initializes a context from known ids.
    /// </summary>
    /// <param name="correlationId">The correlation id spanning the logical operation.</param>
    /// <param name="requestId">The id of this request hop.</param>
    /// <param name="causationId">The id of the causing request or message, if any.</param>
    /// <exception cref="ArgumentNullException"><paramref name="correlationId"/> or <paramref name="requestId"/> is null.</exception>
    public CorrelationContext(string correlationId, string requestId, string? causationId = null)
    {
        CorrelationId = Guard.NotNullOrWhiteSpace(correlationId, nameof(correlationId));
        RequestId = Guard.NotNullOrWhiteSpace(requestId, nameof(requestId));
        CausationId = causationId;
    }

    /// <inheritdoc />
    public string CorrelationId { get; }

    /// <inheritdoc />
    public string RequestId { get; }

    /// <inheritdoc />
    public string? CausationId { get; }

    /// <summary>
    /// Starts a fresh correlation (no inbound id was supplied).
    /// </summary>
    /// <returns>A context with new correlation and request ids and no causation.</returns>
    public static CorrelationContext StartNew()
    {
        string id = NewId();
        return new CorrelationContext(id, NewId(), null);
    }

    /// <summary>
    /// Continues an inbound correlation: keeps the caller's correlation id,
    /// assigns a fresh request id, and records the caller's request as cause.
    /// </summary>
    /// <param name="inboundCorrelationId">The caller-supplied correlation id.</param>
    /// <param name="inboundRequestId">The caller's request id, if known.</param>
    /// <returns>The continued context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="inboundCorrelationId"/> is null or blank.</exception>
    public static CorrelationContext Continue(string inboundCorrelationId, string? inboundRequestId = null)
        => new(
            Guard.NotNullOrWhiteSpace(inboundCorrelationId, nameof(inboundCorrelationId)),
            NewId(),
            inboundRequestId);

    private static string NewId() => Guid.NewGuid().ToString("N");
}
