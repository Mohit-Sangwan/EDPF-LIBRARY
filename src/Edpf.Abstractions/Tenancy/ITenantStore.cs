using System;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Tenancy;

/// <summary>
/// Provisioning store for tenants (§10.1 Tenancy). Backed by the platform
/// database; cached aggressively by implementations — tenant resolution is on
/// every request path.
/// </summary>
public interface ITenantStore
{
    /// <summary>
    /// Loads a tenant by id.
    /// </summary>
    /// <param name="tenantId">The tenant id.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>
    /// The descriptor, or failure with <see cref="ErrorCodes.TenantScopeViolation"/>
    /// when the tenant does not exist or is suspended.
    /// </returns>
    Task<Result<TenantDescriptor>> GetAsync(Guid tenantId, CancellationToken cancellationToken);
}
