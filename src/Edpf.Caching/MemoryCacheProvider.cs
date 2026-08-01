using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Caching;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Caching;

/// <summary>
/// The L1 in-memory cache with **stampede protection** (Phase 15 §④).
/// </summary>
/// <remarks>
/// <para>
/// The protection is the reason this class exists rather than a
/// <c>ConcurrentDictionary</c>. When a hot key expires, every in-flight
/// request misses simultaneously and calls the origin — so the cache delivers
/// its full accumulated load to the database at one instant. That converts a
/// routine expiry into an outage, and it happens precisely when the cache was
/// doing the most good.
/// </para>
/// <para>
/// Here a per-key semaphore admits exactly one caller to the factory; the
/// rest wait and observe the value it produced. A thousand concurrent
/// requests on an expired key make one origin call.
/// </para>
/// </remarks>
public sealed class MemoryCacheProvider : ICacheProvider, IDisposable
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly IClock _clock;
    private bool _disposed;

    /// <summary>
    /// Initializes the cache.
    /// </summary>
    /// <param name="clock">Time source (Z.3 rule 4), so expiry is testable without sleeping.</param>
    public MemoryCacheProvider(IClock clock) => _clock = Guard.NotNull(clock, nameof(clock));

    /// <summary>An in-memory cache is always reachable.</summary>
    public bool IsAvailable => true;

    /// <summary>Counts factory invocations. Exposed so the stampede test can assert "exactly one".</summary>
    public int FactoryInvocations => _factoryInvocations;

    private int _factoryInvocations;

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(CacheKey key, CancellationToken cancellationToken)
        where T : class
    {
        Guard.NotNull(key, nameof(key));
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(TryRead<T>(key));
    }

    /// <inheritdoc />
    public async Task<T> GetOrCreateAsync<T>(
        CacheKey key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan expiry,
        CancellationToken cancellationToken)
        where T : class
    {
        Guard.NotNull(key, nameof(key));
        Guard.NotNull(factory, nameof(factory));

        T? hit = TryRead<T>(key);
        if (hit is not null)
        {
            return hit;
        }

        SemaphoreSlim gate = _locks.GetOrAdd(key.Value, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check inside the gate: while waiting, the admitted caller
            // will have populated the entry, and this is where the other 999
            // requests get their value instead of hitting the origin.
            hit = TryRead<T>(key);
            if (hit is not null)
            {
                return hit;
            }

            Interlocked.Increment(ref _factoryInvocations);
            T created = await factory(cancellationToken).ConfigureAwait(false);
            _entries[key.Value] = new Entry(created, _clock.UtcNow.Add(expiry));
            return created;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public Task<Result> SetAsync<T>(CacheKey key, T value, TimeSpan expiry, CancellationToken cancellationToken)
        where T : class
    {
        Guard.NotNull(key, nameof(key));
        Guard.NotNull(value, nameof(value));
        cancellationToken.ThrowIfCancellationRequested();

        _entries[key.Value] = new Entry(value, _clock.UtcNow.Add(expiry));
        return Task.FromResult(Result.Success());
    }

    /// <inheritdoc />
    public Task<Result> RemoveAsync(CacheKey key, CancellationToken cancellationToken)
    {
        Guard.NotNull(key, nameof(key));
        cancellationToken.ThrowIfCancellationRequested();

        _entries.TryRemove(key.Value, out _);
        return Task.FromResult(Result.Success());
    }

    private T? TryRead<T>(CacheKey key)
        where T : class
    {
        if (!_entries.TryGetValue(key.Value, out Entry? entry))
        {
            return null;
        }

        if (entry.ExpiresUtc <= _clock.UtcNow)
        {
            _entries.TryRemove(key.Value, out _);
            return null;
        }

        return entry.Value as T;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (SemaphoreSlim gate in _locks.Values)
        {
            gate.Dispose();
        }

        _disposed = true;
    }

    private sealed class Entry(object value, DateTimeOffset expiresUtc)
    {
        internal object Value { get; } = value;

        internal DateTimeOffset ExpiresUtc { get; } = expiresUtc;
    }
}

/// <summary>
/// Builds tenant-scoped cache keys. There is no method here that can produce
/// an unprefixed key for a tenant-scoped entity — the guarantee lives in
/// <see cref="CacheKey"/>, so no implementation of this interface can weaken
/// it (Phase 15 §④).
/// </summary>
public sealed class CacheKeyBuilder : ICacheKeyBuilder
{
    /// <inheritdoc />
    public CacheKey ForEntity<TEntity>(Guid tenantId, string id)
        => CacheKey.ForTenant(tenantId, typeof(TEntity).Name, Guard.NotNullOrWhiteSpace(id, nameof(id)));

    /// <inheritdoc />
    public CacheKey ForQuery<TEntity>(Guid tenantId, string queryHash)
        => CacheKey.ForTenant(
            tenantId, typeof(TEntity).Name, "q", Guard.NotNullOrWhiteSpace(queryHash, nameof(queryHash)));
}
