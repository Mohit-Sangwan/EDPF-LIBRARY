using System;

namespace Edpf.Abstractions.Primitives;

/// <summary>
/// Ambient access to the current correlation context, mirroring
/// <c>ITenantContextAccessor</c>. Set once per logical operation at the
/// pipeline edge (ADR-012 stage 1); read by logging, audit and outbox below it.
/// </summary>
public interface ICorrelationContextAccessor
{
    /// <summary>The current correlation context, or null outside a pipeline scope.</summary>
    ICorrelationContext? Current { get; }

    /// <summary>
    /// Sets the ambient correlation for the current async flow. Dispose the
    /// returned scope to restore the previous value.
    /// </summary>
    /// <param name="context">The correlation context to make ambient.</param>
    /// <returns>A scope that restores the prior context on dispose.</returns>
    IDisposable Push(ICorrelationContext context);
}
