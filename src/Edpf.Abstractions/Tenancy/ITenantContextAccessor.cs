using System;

namespace Edpf.Abstractions.Tenancy;

/// <summary>
/// Ambient access to the current tenant (C4 §12.6 <c>TenantContextAccessor</c>).
/// Flows with the async execution context; set once per logical operation by
/// the resolution stage, read by every tenant-scoped component below it.
/// </summary>
public interface ITenantContextAccessor
{
    /// <summary>
    /// The current tenant, or null when no tenant has been resolved.
    /// Repositories treat null as a hard failure (EDPF0009), never as
    /// "all tenants".
    /// </summary>
    ITenantContext? Current { get; }

    /// <summary>
    /// Sets the ambient tenant for the current async flow. Dispose the returned
    /// scope to restore the previous value (supports nested administrative flows).
    /// </summary>
    /// <param name="context">The resolved tenant.</param>
    /// <returns>A scope that restores the prior context on dispose.</returns>
    IDisposable Push(ITenantContext context);
}
