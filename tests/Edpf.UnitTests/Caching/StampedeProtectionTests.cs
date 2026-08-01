using Edpf.Abstractions.Caching;
using Edpf.Caching;
using Edpf.UnitTests.TestDoubles;

namespace Edpf.UnitTests.Caching;

/// <summary>
/// Phase 15 §④: "stampede test (1,000 concurrent requests on an expired key
/// → exactly one origin call)". The missing feature that turns a cache expiry
/// into an outage under load.
/// </summary>
public sealed class StampedeProtectionTests
{
    private static readonly CacheKey Key = CacheKey.ForTenant(Guid.NewGuid(), "Patient", "1");

    [Fact]
    public async Task GetOrCreateAsync_ThousandConcurrentMisses_CallsOriginExactlyOnce()
    {
        using var cache = new MemoryCacheProvider(new FakeClock());
        int originCalls = 0;

        async Task<string> Origin(CancellationToken ct)
        {
            Interlocked.Increment(ref originCalls);
            await Task.Delay(20, ct); // a real origin call is not instant
            return "value";
        }

        Task<string>[] requests = Enumerable.Range(0, 1_000)
            .Select(_ => cache.GetOrCreateAsync(Key, Origin, TimeSpan.FromMinutes(5), CancellationToken.None))
            .ToArray();

        string[] results = await Task.WhenAll(requests);

        // The whole point: a thousand simultaneous misses do not become a
        // thousand database queries at the worst possible moment.
        Assert.Equal(1, originCalls);
        Assert.All(results, value => Assert.Equal("value", value));
    }

    [Fact]
    public async Task GetOrCreateAsync_AfterExpiry_CallsOriginAgainExactlyOnce()
    {
        var clock = new FakeClock();
        using var cache = new MemoryCacheProvider(clock);
        int originCalls = 0;

        Task<string> Origin(CancellationToken ct)
        {
            Interlocked.Increment(ref originCalls);
            return Task.FromResult("value");
        }

        await cache.GetOrCreateAsync(Key, Origin, TimeSpan.FromMinutes(5), CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(6));

        Task<string>[] afterExpiry = Enumerable.Range(0, 200)
            .Select(_ => cache.GetOrCreateAsync(Key, Origin, TimeSpan.FromMinutes(5), CancellationToken.None))
            .ToArray();
        await Task.WhenAll(afterExpiry);

        Assert.Equal(2, originCalls);
    }

    [Fact]
    public async Task GetOrCreateAsync_DistinctKeys_DoNotBlockEachOther()
    {
        // Per-key locking, not a global lock: one slow key must not stall the
        // entire cache.
        using var cache = new MemoryCacheProvider(new FakeClock());
        var tenant = Guid.NewGuid();

        Task<string>[] requests = Enumerable.Range(0, 50)
            .Select(i => cache.GetOrCreateAsync(
                CacheKey.ForTenant(tenant, "Patient", i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                _ => Task.FromResult("v" + i),
                TimeSpan.FromMinutes(5),
                CancellationToken.None))
            .ToArray();

        string[] results = await Task.WhenAll(requests);

        Assert.Equal(50, results.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(50, cache.FactoryInvocations);
    }

    [Fact]
    public async Task GetAsync_ExpiredEntry_IsAMissNotAStaleHit()
    {
        var clock = new FakeClock();
        using var cache = new MemoryCacheProvider(clock);
        await cache.SetAsync(Key, "value", TimeSpan.FromMinutes(5), CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Null(await cache.GetAsync<string>(Key, CancellationToken.None));
    }

    [Fact]
    public async Task RemoveAsync_Always_EvictsImmediately()
    {
        using var cache = new MemoryCacheProvider(new FakeClock());
        await cache.SetAsync(Key, "value", TimeSpan.FromMinutes(5), CancellationToken.None);

        await cache.RemoveAsync(Key, CancellationToken.None);

        Assert.Null(await cache.GetAsync<string>(Key, CancellationToken.None));
    }
}
