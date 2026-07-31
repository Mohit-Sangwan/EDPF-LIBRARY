using Microsoft.EntityFrameworkCore;

namespace Edpf.WalkingSkeleton.Api.Infrastructure.Persistence;

/// <summary>
/// Seeds the two demonstration tenants of the gate script (Phase 02 §⑤:
/// tenant A creates, tenant B must see 404). Stable ids so the demo script
/// and integration tests can reference them.
/// </summary>
public static class SkeletonSeeder
{
    /// <summary>Tenant A — "Aurora Health", region IN.</summary>
    public static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    /// <summary>Tenant B — "Borealis Clinic", region EU.</summary>
    public static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    /// <summary>Inserts the demo tenants when absent. Idempotent.</summary>
    public static async Task SeedAsync(SkeletonDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (await db.Tenants.AnyAsync())
        {
            return;
        }

        db.Tenants.AddRange(
            new TenantRow
            {
                Id = TenantA,
                Name = "Aurora Health",
                Region = "in-south-1",
                IsolationMode = 0, // SharedSchema (ADR-004 default)
                KekReference = Guid.NewGuid(),
            },
            new TenantRow
            {
                Id = TenantB,
                Name = "Borealis Clinic",
                Region = "eu-central-1",
                IsolationMode = 0,
                KekReference = Guid.NewGuid(),
            });

        await db.SaveChangesAsync();
    }
}
