using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Tenancy;

/// <summary>
/// Resolves a tenant from a host-supplied resolution key (Phase 01: the seam
/// Phase 12 fills). Hosts extract the key from their transport — a header, a
/// claim, a host name — and this seam turns it into a tenant, or refuses.
/// </summary>
public interface ITenantResolver
{
    /// <summary>
    /// Resolves the tenant identified by <paramref name="resolutionKey"/>.
    /// </summary>
    /// <param name="resolutionKey">The transport-extracted key (e.g. the <c>X-Tenant-Id</c> header value).</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <returns>
    /// The tenant descriptor, or a failure with <see cref="ErrorCodes.TenantScopeViolation"/> —
    /// an unknown tenant is indistinguishable from a forbidden one.
    /// </returns>
    Task<Result<TenantDescriptor>> ResolveAsync(string resolutionKey, CancellationToken cancellationToken);
}
