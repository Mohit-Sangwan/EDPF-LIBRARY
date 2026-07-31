using System;

namespace Edpf.Abstractions.Tenancy;

/// <summary>
/// A tenant's provisioning record as held by the <see cref="ITenantStore"/>.
/// Immutable snapshot; contains no key material, only the KEK reference.
/// </summary>
public sealed class TenantDescriptor : ITenantContext
{
    /// <summary>
    /// Initializes a descriptor.
    /// </summary>
    /// <param name="tenantId">The tenant id. Must not be empty.</param>
    /// <param name="name">Display name. Classified Internal; never appears in logs at Phi paths.</param>
    /// <param name="region">The pinned data region (ADR-010).</param>
    /// <param name="isolationMode">The provisioned isolation mode (ADR-004).</param>
    /// <param name="kekReference">Reference to the tenant KEK (ADR-007).</param>
    /// <exception cref="ArgumentException"><paramref name="tenantId"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="region"/> is null.</exception>
    public TenantDescriptor(
        Guid tenantId,
        string name,
        string region,
        TenantIsolationMode isolationMode,
        Guid kekReference)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        TenantId = tenantId;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Region = region ?? throw new ArgumentNullException(nameof(region));
        IsolationMode = isolationMode;
        KekReference = kekReference;
    }

    /// <inheritdoc />
    public Guid TenantId { get; }

    /// <summary>Display name of the tenant.</summary>
    public string Name { get; }

    /// <inheritdoc />
    public string Region { get; }

    /// <inheritdoc />
    public TenantIsolationMode IsolationMode { get; }

    /// <inheritdoc />
    public Guid KekReference { get; }
}
