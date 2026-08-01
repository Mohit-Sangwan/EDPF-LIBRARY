using System;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Caching;

/// <summary>
/// Cache access (Phase 15 §④). Two behaviours are contractual rather than
/// optional: stampede protection, and degrading to slow rather than broken
/// when the cache is unavailable.
/// </summary>
public interface ICacheProvider
{
    /// <summary>
    /// Reads a cached value.
    /// </summary>
    /// <typeparam name="T">The cached type.</typeparam>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The value, or null on a miss. A miss is not a failure.</returns>
    Task<T?> GetAsync<T>(CacheKey key, CancellationToken cancellationToken)
        where T : class;

    /// <summary>
    /// Returns the cached value, computing and storing it on a miss.
    /// </summary>
    /// <typeparam name="T">The cached type.</typeparam>
    /// <param name="key">The key.</param>
    /// <param name="factory">Computes the value on a miss.</param>
    /// <param name="expiry">How long the entry lives.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The cached or freshly computed value.</returns>
    /// <remarks>
    /// **Stampede protection is part of this contract.** When a popular key
    /// expires, a thousand concurrent requests must produce exactly one call
    /// to <paramref name="factory"/>; the rest wait for it. Without this, an
    /// expiry becomes an outage — the origin receives the full uncached load
    /// at the worst possible moment, which is precisely when the cache was
    /// most valuable.
    /// </remarks>
    Task<T> GetOrCreateAsync<T>(
        CacheKey key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan expiry,
        CancellationToken cancellationToken)
        where T : class;

    /// <summary>
    /// Stores a value.
    /// </summary>
    /// <typeparam name="T">The cached type.</typeparam>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <param name="expiry">How long the entry lives.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>
    /// Success, or a failure the caller may ignore — a cache write that fails
    /// must never fail the request that triggered it.
    /// </returns>
    Task<Result> SetAsync<T>(CacheKey key, T value, TimeSpan expiry, CancellationToken cancellationToken)
        where T : class;

    /// <summary>
    /// Removes an entry.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">Cancels the removal.</param>
    /// <returns>Success once removed or already absent.</returns>
    Task<Result> RemoveAsync(CacheKey key, CancellationToken cancellationToken);

    /// <summary>
    /// True when the backing cache is currently reachable. A false value
    /// means requests are being served from origin — slower, but correct.
    /// </summary>
    bool IsAvailable { get; }
}

/// <summary>
/// Builds cache keys from entities (Phase 15 §④). Implementations must not
/// expose any path to an unprefixed key for a tenant-scoped entity — the
/// property is enforced by <see cref="CacheKey"/> itself.
/// </summary>
public interface ICacheKeyBuilder
{
    /// <summary>
    /// Builds the key for one entity instance.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="tenantId">The owning tenant.</param>
    /// <param name="id">The entity id.</param>
    /// <returns>A tenant-scoped key.</returns>
    CacheKey ForEntity<TEntity>(Guid tenantId, string id);

    /// <summary>
    /// Builds the key for a query result.
    /// </summary>
    /// <typeparam name="TEntity">The queried entity type.</typeparam>
    /// <param name="tenantId">The owning tenant.</param>
    /// <param name="queryHash">A stable hash of the query shape and parameters.</param>
    /// <returns>A tenant-scoped key.</returns>
    CacheKey ForQuery<TEntity>(Guid tenantId, string queryHash);
}
