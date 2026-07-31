using Edpf.Abstractions.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Edpf.WalkingSkeleton.Api.Infrastructure.Persistence;

/// <summary>
/// The skeleton's EF Core context (ADR-001: wrap EF Core, do not build an ORM).
/// The tenant boundary is enforced here structurally: a global query filter on
/// every tenant-scoped set, driven by the ambient tenant context — a request
/// without a resolved tenant reads nothing, and a cross-tenant id simply does
/// not exist (404 semantics, EDPF-AUTHZ-2102).
/// </summary>
public sealed class SkeletonDbContext(
    DbContextOptions<SkeletonDbContext> options,
    ITenantContextAccessor tenantAccessor) : DbContext(options)
{
    private readonly ITenantContextAccessor _tenantAccessor = tenantAccessor;

    /// <summary>The current tenant id, or <see cref="Guid.Empty"/> when unresolved (matches no rows).</summary>
    public Guid CurrentTenantId => _tenantAccessor.Current?.TenantId ?? Guid.Empty;

    public DbSet<PatientRow> Patients => Set<PatientRow>();
    public DbSet<TenantRow> Tenants => Set<TenantRow>();
    public DbSet<KeyRow> Keys => Set<KeyRow>();
    public DbSet<AuditRow> AuditEvents => Set<AuditRow>();
    public DbSet<OutboxRow> Outbox => Set<OutboxRow>();
    public DbSet<IdempotencyRow> IdempotencyRecords => Set<IdempotencyRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PatientRow>(b =>
        {
            b.ToTable("PATIENT");
            b.HasKey(p => new { p.TenantId, p.Id }); // Z.2: TenantId leads every clustered index.
            b.Property(p => p.GivenName).HasMaxLength(128);
            b.Property(p => p.FamilyName).HasMaxLength(128);
            b.Property(p => p.MrnEnvelope).IsRequired();
            b.HasQueryFilter(p => p.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<TenantRow>(b =>
        {
            b.ToTable("TENANT");
            b.HasKey(t => t.Id);
            b.Property(t => t.Name).HasMaxLength(256);
            b.Property(t => t.Region).HasMaxLength(64);
        });

        modelBuilder.Entity<KeyRow>(b =>
        {
            b.ToTable("KEY_STORE");
            b.HasKey(k => k.KeyId);
            b.HasIndex(k => new { k.TenantId, k.SubjectId, k.Purpose });
        });

        modelBuilder.Entity<AuditRow>(b =>
        {
            b.ToTable("AUDIT_EVENT");
            b.HasKey(a => a.AuditId);
            // The chain's fork-prevention: two writers cannot append the same
            // sequence for a tenant (C4 §12.3 optimistic head concurrency).
            b.HasIndex(a => new { a.TenantId, a.Sequence }).IsUnique();
            b.Property(a => a.EventType).HasMaxLength(128);
            b.Property(a => a.SubjectToken).HasMaxLength(64);
            b.Property(a => a.CorrelationId).HasMaxLength(64);
        });

        modelBuilder.Entity<OutboxRow>(b =>
        {
            b.ToTable("OUTBOX_MESSAGE");
            b.HasKey(o => o.Id);
            b.HasIndex(o => o.DispatchedUtc);
            b.Property(o => o.MessageType).HasMaxLength(128);
            b.Property(o => o.CorrelationId).HasMaxLength(64);
        });

        modelBuilder.Entity<IdempotencyRow>(b =>
        {
            b.ToTable("IDEMPOTENCY_RECORD");
            b.HasKey(i => new { i.TenantId, i.IdempotencyKey });
            b.Property(i => i.IdempotencyKey).HasMaxLength(128);
            b.Property(i => i.RequestHash).HasMaxLength(64);
        });
    }
}
