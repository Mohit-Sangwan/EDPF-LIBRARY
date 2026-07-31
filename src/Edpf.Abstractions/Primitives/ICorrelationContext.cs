namespace Edpf.Abstractions.Primitives;

/// <summary>
/// Ambient correlation identifiers (Phase 01 shared kernel). Established at
/// the pipeline edge (ADR-012 stage 1) and flowed through every log entry,
/// trace, audit record and error response — one id from request to response.
/// </summary>
public interface ICorrelationContext
{
    /// <summary>
    /// The correlation id spanning an entire logical operation, across service
    /// boundaries. Propagated via the <c>X-Correlation-Id</c> header.
    /// </summary>
    string CorrelationId { get; }

    /// <summary>The id of the current request (one hop within the correlation).</summary>
    string RequestId { get; }

    /// <summary>
    /// The id of the message or request that caused this one, if any —
    /// gives outbox consumers and sagas an auditable causal chain (ADR-003).
    /// </summary>
    string? CausationId { get; }
}
