using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Tenancy;
using Edpf.WalkingSkeleton.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Edpf.WalkingSkeleton.Api.Infrastructure.Tenancy;

/// <summary>
/// EF-backed tenant provisioning store. Unknown and suspended tenants are the
/// same failure as forbidden ones (EDPF-AUTHZ-2102) — existence is never
/// disclosed at the boundary.
/// </summary>
public sealed class EfTenantStore(SkeletonDbContext db) : ITenantStore
{
    public async Task<Result<TenantDescriptor>> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        TenantRow? row = await db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

        if (row is null)
        {
            return Result.Failure<TenantDescriptor>(NotResolvable());
        }

        return Result.Success(new TenantDescriptor(
            row.Id, row.Name, row.Region, (TenantIsolationMode)row.IsolationMode, row.KekReference));
    }

    internal static Error NotResolvable() => new(
        ErrorCodes.TenantScopeViolation,
        "The requested resource was not found.",
        ErrorCategory.NotFound);
}

/// <summary>
/// Header-based tenant resolution (Phase 02 §④: header strategy; claim and
/// host strategies arrive in Phase 12). A malformed key resolves exactly like
/// an unknown one.
/// </summary>
public sealed class HeaderTenantResolver(ITenantStore store) : ITenantResolver
{
    public Task<Result<TenantDescriptor>> ResolveAsync(string resolutionKey, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(resolutionKey, out Guid tenantId) || tenantId == Guid.Empty)
        {
            return Task.FromResult(Result.Failure<TenantDescriptor>(EfTenantStore.NotResolvable()));
        }

        return store.GetAsync(tenantId, cancellationToken);
    }
}
