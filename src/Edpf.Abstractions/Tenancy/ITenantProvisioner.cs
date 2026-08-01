using System;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Security;

namespace Edpf.Abstractions.Tenancy;

/// <summary>
/// Creates and destroys tenants (Phase 12 §③). Provisioning runs as an
/// atomic, resumable saga: create tenant → provision schema/database →
/// generate keys → seed → **verify isolation**. The verification step is not
/// optional — a tenant that exists but is not provably isolated is worse than
/// no tenant.
/// </summary>
public interface ITenantProvisioner
{
    /// <summary>
    /// Provisions a new tenant.
    /// </summary>
    /// <param name="request">What to provision.</param>
    /// <param name="cancellationToken">Cancels between saga steps.</param>
    /// <returns>
    /// The provisioned tenant, or a failure. On failure the saga compensates,
    /// so a half-provisioned tenant is never left behind.
    /// </returns>
    Task<Result<TenantDescriptor>> ProvisionAsync(
        TenantProvisioningRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Deprovisions a tenant: export → verify → crypto-shred → audit.
    /// </summary>
    /// <param name="tenantId">The tenant to destroy.</param>
    /// <param name="justification">Why. Recorded in the audit trail; required.</param>
    /// <param name="cancellationToken">Cancels before destruction begins.</param>
    /// <returns>
    /// Success, or failure with <see cref="ErrorCodes.LegalHold"/> when a hold
    /// blocks destruction. The hold check happens **before** the export, so a
    /// blocked deprovision changes nothing.
    /// </returns>
    Task<Result> DeprovisionAsync(Guid tenantId, string justification, CancellationToken cancellationToken);
}

/// <summary>What to provision.</summary>
public sealed class TenantProvisioningRequest
{
    /// <summary>
    /// Initializes a request.
    /// </summary>
    /// <param name="name">Display name.</param>
    /// <param name="region">Pinned data region (ADR-010). Immutable after provisioning.</param>
    /// <param name="isolationMode">Isolation mode (ADR-004).</param>
    /// <exception cref="ArgumentException">Any argument is blank.</exception>
    public TenantProvisioningRequest(string name, string region, TenantIsolationMode isolationMode)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Tenant name must not be blank.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(region))
        {
            throw new ArgumentException("Region must not be blank.", nameof(region));
        }

        Name = name;
        Region = region;
        IsolationMode = isolationMode;
    }

    /// <summary>Display name.</summary>
    public string Name { get; }

    /// <summary>Pinned data region. Changing it later would move data across a legal boundary.</summary>
    public string Region { get; }

    /// <summary>Isolation mode. A single deployment may host all three.</summary>
    public TenantIsolationMode IsolationMode { get; }
}

/// <summary>
/// Resolves per-tenant key material (Phase 12 §③). Each tenant's DEKs are
/// wrapped by its own KEK, which is what makes tenant-scoped crypto-shredding
/// possible and gives a **cryptographic** boundary on top of the logical one.
/// </summary>
public interface ITenantKeyProvider
{
    /// <summary>
    /// Resolves the tenant's key-encryption key.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <returns>The KEK handle, or failure when the tenant was shredded.</returns>
    Task<Result<KeyHandle>> GetTenantKekAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Rotates the tenant KEK, re-wrapping its DEKs without re-encrypting any
    /// data — the property that makes rotation a zero-downtime operation.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="cancellationToken">Cancels before the rotation commits.</param>
    /// <returns>The new key version.</returns>
    Task<Result<int>> RotateTenantKekAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// Per-tenant limits, the noisy-neighbour control for shared-schema
/// deployments (Phase 12 §③).
/// </summary>
public sealed class TenantQuota
{
    /// <summary>
    /// Initializes a quota.
    /// </summary>
    /// <param name="maxRequestsPerMinute">Request rate ceiling.</param>
    /// <param name="maxStorageBytes">Blob storage ceiling.</param>
    /// <param name="maxConcurrentConnections">Connection-pool share.</param>
    public TenantQuota(int maxRequestsPerMinute, long maxStorageBytes, int maxConcurrentConnections)
    {
        MaxRequestsPerMinute = maxRequestsPerMinute;
        MaxStorageBytes = maxStorageBytes;
        MaxConcurrentConnections = maxConcurrentConnections;
    }

    /// <summary>Requests per minute before <see cref="ErrorCodes.RateLimited"/>.</summary>
    public int MaxRequestsPerMinute { get; }

    /// <summary>Total blob bytes this tenant may hold.</summary>
    public long MaxStorageBytes { get; }

    /// <summary>Connections this tenant may hold concurrently, so one tenant cannot drain the pool.</summary>
    public int MaxConcurrentConnections { get; }
}

/// <summary>
/// Time-bounded, audited authority to act outside one's tenant
/// (Phase 12 §③). Cross-tenant administration is not a role; it is a grant
/// with an expiry and a written reason.
/// </summary>
public interface IBreakGlassService
{
    /// <summary>
    /// Requests cross-tenant authority.
    /// </summary>
    /// <param name="operatorId">Who is asking.</param>
    /// <param name="targetTenantId">Which tenant they need to reach.</param>
    /// <param name="justification">Why. Mandatory, free text, audited verbatim.</param>
    /// <param name="duration">How long the grant lasts. Bounded by policy.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The grant, or refusal.</returns>
    Task<Result<BreakGlassGrant>> RequestAsync(
        string operatorId,
        Guid targetTenantId,
        string justification,
        TimeSpan duration,
        CancellationToken cancellationToken);

    /// <summary>
    /// Checks whether a live grant authorises this operator for this tenant.
    /// </summary>
    /// <param name="operatorId">The operator.</param>
    /// <param name="targetTenantId">The tenant.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>True only while an unexpired grant exists.</returns>
    Task<bool> IsAuthorizedAsync(string operatorId, Guid targetTenantId, CancellationToken cancellationToken);
}

/// <summary>A granted, expiring cross-tenant authority.</summary>
public sealed class BreakGlassGrant
{
    /// <summary>
    /// Initializes a grant.
    /// </summary>
    /// <param name="grantId">Grant identifier, referenced by audit records.</param>
    /// <param name="operatorId">Who holds it.</param>
    /// <param name="targetTenantId">Which tenant it reaches.</param>
    /// <param name="justification">Why it was granted.</param>
    /// <param name="expiresUtc">When it lapses.</param>
    /// <exception cref="ArgumentException"><paramref name="justification"/> is blank.</exception>
    public BreakGlassGrant(
        Guid grantId, string operatorId, Guid targetTenantId, string justification, DateTimeOffset expiresUtc)
    {
        if (string.IsNullOrWhiteSpace(justification))
        {
            throw new ArgumentException(
                "Break-glass requires a written justification; it is the record that makes the access reviewable.",
                nameof(justification));
        }

        GrantId = grantId;
        OperatorId = operatorId ?? throw new ArgumentNullException(nameof(operatorId));
        TargetTenantId = targetTenantId;
        Justification = justification;
        ExpiresUtc = expiresUtc;
    }

    /// <summary>Grant identifier.</summary>
    public Guid GrantId { get; }

    /// <summary>Who holds the grant.</summary>
    public string OperatorId { get; }

    /// <summary>Which tenant it reaches.</summary>
    public Guid TargetTenantId { get; }

    /// <summary>The written justification.</summary>
    public string Justification { get; }

    /// <summary>When it lapses.</summary>
    public DateTimeOffset ExpiresUtc { get; }

    /// <summary>True while the grant is still live.</summary>
    /// <param name="now">The current instant, from <see cref="IClock"/>.</param>
    public bool IsLive(DateTimeOffset now) => now < ExpiresUtc;
}
