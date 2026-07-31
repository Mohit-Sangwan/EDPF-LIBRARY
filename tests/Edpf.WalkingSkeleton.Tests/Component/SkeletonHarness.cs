using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Tenancy;
using Edpf.Core.Tenancy;
using Edpf.WalkingSkeleton.Api.Infrastructure.Persistence;
using Edpf.WalkingSkeleton.Api.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Edpf.WalkingSkeleton.Tests.Component;

/// <summary>Deterministic clock for component tests (Z.7: no wall-clock reads).</summary>
public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
}

/// <summary>
/// In-memory relational harness for the skeleton's security/audit components:
/// a live Sqlite connection, a tenant pushed onto the ambient context, and
/// the real crypto/KMS/audit implementations wired together.
/// </summary>
public sealed class SkeletonHarness : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDisposable _tenantScope;

    public SkeletonHarness()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        TenantAccessor = new TenantContextAccessor();
        DbContextOptions<SkeletonDbContext> options = new DbContextOptionsBuilder<SkeletonDbContext>()
            .UseSqlite(_connection)
            .Options;
        Db = new SkeletonDbContext(options, TenantAccessor);
        Db.Database.EnsureCreated();

        TenantId = Guid.NewGuid();
        _tenantScope = TenantAccessor.Push(new TenantDescriptor(
            TenantId, "test-tenant", "in-south-1", TenantIsolationMode.SharedSchema, Guid.NewGuid()));

        Clock = new FakeClock();
        Registry = new AlgorithmRegistry();
        Hashing = new HashingService();
        Kms = new KeyManagementService(Db, Registry, Clock, Options.Create(new KeyManagementOptions()));
        Crypto = new CryptoProvider(Registry, Kms);
        Tokenizer = new SubjectTokenizer(Kms, Hashing);
    }

    public Guid TenantId { get; }
    public SkeletonDbContext Db { get; }
    public TenantContextAccessor TenantAccessor { get; }
    public FakeClock Clock { get; }
    public AlgorithmRegistry Registry { get; }
    public HashingService Hashing { get; }
    public KeyManagementService Kms { get; }
    public CryptoProvider Crypto { get; }
    public SubjectTokenizer Tokenizer { get; }

    public void Dispose()
    {
        _tenantScope.Dispose();
        Db.Dispose();
        _connection.Dispose();
    }
}
