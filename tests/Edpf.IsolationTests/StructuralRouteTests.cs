using Edpf.Abstractions.Caching;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Search;
using Edpf.Abstractions.Storage;
using Edpf.Abstractions.Tenancy;
using Edpf.Caching;
using Edpf.Core.Tenancy;

namespace Edpf.IsolationTests;

/// <summary>Two tenants used throughout the suite.</summary>
public static class Tenants
{
    /// <summary>Tenant A — the victim.</summary>
    public static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    /// <summary>Tenant B — the attacker, fully authorised within its own tenant.</summary>
    public static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    /// <summary>Builds a tenant context.</summary>
    /// <param name="id">The tenant id.</param>
    public static TenantDescriptor Context(Guid id) =>
        new(id, "t", "in-south-1", TenantIsolationMode.SharedSchema, Guid.NewGuid());
}

/// <summary>
/// Route 6 — blob paths. The strongest form of the guarantee: a cross-tenant
/// path is not merely rejected at the boundary, it is **unconstructable**.
/// </summary>
[CoversIsolationRoute(IsolationRoutes.BlobPath)]
public sealed class BlobPathIsolationTests
{
    [Fact]
    public void BlobPath_Always_BeginsWithItsTenant()
    {
        BlobPath path = BlobPath.Create(Tenants.A, "studies", "study-1.dcm");

        Assert.StartsWith("tenants/" + Tenants.A.ToString("D") + "/", path.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void BlobPath_WithoutTenant_CannotBeConstructed()
    {
        Assert.Throws<ArgumentException>(() => BlobPath.Create(Guid.Empty, "studies", "x.dcm"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../..")]
    [InlineData("../../tenants")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("%2e%2e")]
    [InlineData("%2f")]
    [InlineData("C:")]
    [InlineData("study\0.dcm")]
    [InlineData("study\n.dcm")]
    public void BlobPath_TraversalOrSeparator_IsRejectedNotNormalised(string segment)
    {
        // Rejected, not cleaned: a value needing cleaning did not come from
        // anywhere legitimate, and normalising it hides that.
        Assert.Throws<ArgumentException>(() => BlobPath.Create(Tenants.A, segment));
    }

    [Fact]
    public void BlobPath_TraversalAcrossSegments_CannotEscapeTheTenantRoot()
    {
        // Even if every individual segment were legal, the tenant prefix is
        // prepended by the type and is not caller-supplied.
        Assert.Throws<ArgumentException>(
            () => BlobPath.Create(Tenants.B, "..", "..", "tenants", Tenants.A.ToString("D")));
    }

    [Fact]
    public void BelongsTo_OtherTenantsPath_IsFalse()
    {
        BlobPath victimPath = BlobPath.Create(Tenants.A, "studies", "study-1.dcm");

        Assert.False(BlobPath.BelongsTo(victimPath, Tenants.B));
        Assert.True(BlobPath.BelongsTo(victimPath, Tenants.A));
    }

    [Fact]
    public void BlobPath_SameSegmentsDifferentTenants_AreDifferentPaths()
    {
        BlobPath a = BlobPath.Create(Tenants.A, "studies", "study-1.dcm");
        BlobPath b = BlobPath.Create(Tenants.B, "studies", "study-1.dcm");

        Assert.NotEqual(a, b);
        Assert.NotEqual(a.Value, b.Value);
    }
}

/// <summary>
/// Route 3 — cache key collision. Among the easiest cross-tenant leaks to
/// introduce by accident: <c>"patient:" + id</c> looks obviously correct.
/// </summary>
[CoversIsolationRoute(IsolationRoutes.CacheKey)]
public sealed class CacheKeyIsolationTests
{
    private readonly CacheKeyBuilder _builder = new();

    [Fact]
    public void CacheKey_SameEntityIdDifferentTenants_DoNotCollide()
    {
        CacheKey a = _builder.ForEntity<object>(Tenants.A, "patient-1");
        CacheKey b = _builder.ForEntity<object>(Tenants.B, "patient-1");

        Assert.NotEqual(a, b);
        Assert.NotEqual(a.Value, b.Value);
    }

    [Fact]
    public void CacheKey_TenantScoped_CannotBeBuiltWithoutATenant()
    {
        Assert.Throws<ArgumentException>(() => CacheKey.ForTenant(Guid.Empty, "Patient", "1"));
    }

    [Theory]
    [InlineData("evil:key")]
    [InlineData("wild*card")]
    [InlineData("question?mark")]
    [InlineData("controlchar")]
    public void CacheKey_PartsThatCouldAddressOtherEntries_AreRejected(string hostilePart)
    {
        // A separator would let one key name another's namespace; a glob
        // would let an invalidation sweep across tenants.
        Assert.Throws<ArgumentException>(() => CacheKey.ForTenant(Tenants.A, "Patient", hostilePart));
    }

    [Fact]
    public void CacheKey_GlobalKey_RequiresWrittenJustification()
    {
        // The one key shape that crosses the boundary must be a conscious act.
        Assert.Throws<ArgumentException>(() => CacheKey.Global("  ", "CurrencyTable"));

        CacheKey global = CacheKey.Global("currency rates are not tenant data", "CurrencyTable");
        Assert.False(global.IsTenantScoped);
    }

    [Fact]
    public void IsReadableBy_OtherTenant_IsFalseForTenantScopedKeys()
    {
        CacheKey key = _builder.ForEntity<object>(Tenants.A, "patient-1");

        Assert.False(key.IsReadableBy(Tenants.B));
        Assert.True(key.IsReadableBy(Tenants.A));
    }

    [Fact]
    public void IsReadableBy_AnyTenant_IsTrueForGlobalKeys()
    {
        CacheKey global = CacheKey.Global("reference data", "CurrencyTable");

        Assert.True(global.IsReadableBy(Tenants.A));
        Assert.True(global.IsReadableBy(Tenants.B));
    }

    [Fact]
    public async Task CacheProvider_TwoTenantsSameEntityId_ReadTheirOwnValues()
    {
        using var cache = new MemoryCacheProvider(new FixedClock());
        CacheKey keyA = _builder.ForEntity<string>(Tenants.A, "1");
        CacheKey keyB = _builder.ForEntity<string>(Tenants.B, "1");

        await cache.SetAsync(keyA, "tenant-a-data", TimeSpan.FromMinutes(5), CancellationToken.None);
        await cache.SetAsync(keyB, "tenant-b-data", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.Equal("tenant-a-data", await cache.GetAsync<string>(keyA, CancellationToken.None));
        Assert.Equal("tenant-b-data", await cache.GetAsync<string>(keyB, CancellationToken.None));
    }
}

/// <summary>
/// Route 4 — the search index, including the aggregation side-channel. Facet
/// counts and total hits are classic leaks: they reveal another tenant's data
/// volumes without returning a single document.
/// </summary>
[CoversIsolationRoute(IsolationRoutes.SearchIndex)]
public sealed class SearchIsolationTests
{
    [Fact]
    public void SearchQuery_WithoutTenant_CannotBeExpressed()
    {
        Assert.Throws<ArgumentException>(
            () => new SearchQuery(Guid.Empty, "smith", new PageRequest(1, 10)));
    }

    [Fact]
    public void SearchQuery_Always_CarriesTheCallersTenant()
    {
        var query = new SearchQuery(Tenants.B, "smith", new PageRequest(1, 10));

        Assert.Equal(Tenants.B, query.TenantId);
    }

    [Fact]
    public void SearchResults_TotalHits_IsDocumentedAsPostTrimming()
    {
        // The contract states counts are computed after trimming. This test
        // pins the shape; the engine-bound assertion that a real index honours
        // it is a Gate G3 item requiring a live cluster.
        var results = new SearchResults<string>(
            ["doc-1"],
            totalHits: 1,
            facetCounts: new Dictionary<string, IReadOnlyDictionary<string, long>>());

        Assert.Equal(1, results.TotalHits);
        Assert.Single(results.Documents);
    }
}

/// <summary>Deterministic clock for the suite.</summary>
public sealed class FixedClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; set; } = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
}
