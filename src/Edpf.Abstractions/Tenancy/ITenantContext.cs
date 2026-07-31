using System;

namespace Edpf.Abstractions.Tenancy;

/// <summary>
/// The resolved tenant for the current logical operation (ADR-004; C4 §12.6).
/// Established by the tenant-resolution pipeline stage (ADR-012 stage 2) —
/// before authentication, and always before any data access. Code that reaches
/// a repository without a resolved tenant is a defect, not a configuration
/// choice.
/// </summary>
public interface ITenantContext
{
    /// <summary>The tenant id. Never <see cref="Guid.Empty"/> for a resolved context.</summary>
    Guid TenantId { get; }

    /// <summary>
    /// The tenant's pinned data region (ADR-010). Cross-region reads are
    /// refused by default with an auditable break-glass.
    /// </summary>
    string Region { get; }

    /// <summary>The isolation mode this tenant is provisioned under.</summary>
    TenantIsolationMode IsolationMode { get; }

    /// <summary>
    /// Reference to the tenant's key-encryption key (KEK) that wraps its
    /// data-encryption keys (ADR-004, ADR-007).
    /// </summary>
    Guid KekReference { get; }
}
