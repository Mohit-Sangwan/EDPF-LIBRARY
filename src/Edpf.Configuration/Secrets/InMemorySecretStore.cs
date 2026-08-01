using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Configuration;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Configuration.Secrets;

/// <summary>
/// An in-process secret store for tests and for composing defaults
/// (Phase 03 §④ "plus an in-memory provider for tests"). Never suitable for
/// production: values live unencrypted in process memory for the lifetime of
/// the host.
/// </summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, Entry> _secrets =
        new(StringComparer.Ordinal);

    private readonly IClock _clock;
    private readonly TimeSpan _overlapWindow;

    /// <summary>
    /// Initializes the store.
    /// </summary>
    /// <param name="clock">Time source (Z.3 rule 4).</param>
    /// <param name="overlapWindow">
    /// How long a rotated secret's previous value stays acceptable. Defaults
    /// to <see cref="SecretStoreDefaults.OverlapWindow"/>.
    /// </param>
    public InMemorySecretStore(IClock clock, TimeSpan? overlapWindow = null)
    {
        _clock = Guard.NotNull(clock, nameof(clock));
        _overlapWindow = overlapWindow ?? SecretStoreDefaults.OverlapWindow;
    }

    /// <inheritdoc />
    public string Name => "in-memory";

    /// <inheritdoc />
    public Task<Result<SecretValue>> GetAsync(string key, CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(key, nameof(key));
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_secrets.TryGetValue(key, out Entry? entry)
            ? Result.Success(entry.Current)
            : Result.Failure<SecretValue>(SecretErrors.NotFound(key)));
    }

    /// <inheritdoc />
    public Task<Result<SecretRotationView>> GetForRotationAsync(string key, CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(key, nameof(key));
        cancellationToken.ThrowIfCancellationRequested();

        if (!_secrets.TryGetValue(key, out Entry? entry))
        {
            return Task.FromResult(Result.Failure<SecretRotationView>(SecretErrors.NotFound(key)));
        }

        bool overlapOpen = entry.Previous is not null
            && entry.OverlapExpiresUtc is { } expiry
            && _clock.UtcNow < expiry;

        return Task.FromResult(Result.Success(new SecretRotationView(
            entry.Current,
            overlapOpen ? entry.Previous : null,
            overlapOpen ? entry.OverlapExpiresUtc : null)));
    }

    /// <inheritdoc />
    public Task<Result> SetAsync(string key, SecretValue value, CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(key, nameof(key));
        Guard.NotNull(value, nameof(value));
        cancellationToken.ThrowIfCancellationRequested();

        _secrets.AddOrUpdate(
            key,
            _ => new Entry(value, null, null),
            (_, existing) => new Entry(value, existing.Current, _clock.UtcNow.Add(_overlapWindow)));

        return Task.FromResult(Result.Success());
    }

    private sealed class Entry(SecretValue current, SecretValue? previous, DateTimeOffset? overlapExpiresUtc)
    {
        internal SecretValue Current { get; } = current;
        internal SecretValue? Previous { get; } = previous;
        internal DateTimeOffset? OverlapExpiresUtc { get; } = overlapExpiresUtc;
    }
}

/// <summary>Shared secret-store defaults.</summary>
public static class SecretStoreDefaults
{
    /// <summary>
    /// How long a rotated secret's previous value remains acceptable. Long
    /// enough for every node to observe the rotation, short enough that a
    /// compromised credential is not honoured indefinitely.
    /// </summary>
    public static TimeSpan OverlapWindow { get; } = TimeSpan.FromMinutes(15);
}

/// <summary>Failures common to every secret store. Messages name keys, never values.</summary>
public static class SecretErrors
{
    /// <summary>The requested secret does not exist.</summary>
    /// <param name="key">The key that was requested.</param>
    public static Error NotFound(string key) => new(
        ErrorCodes.ConfigurationInvalid,
        "Secret '" + key + "' is not present in the configured store.",
        ErrorCategory.Configuration);

    /// <summary>The store cannot accept writes.</summary>
    /// <param name="storeName">The read-only store.</param>
    public static Error ReadOnly(string storeName) => new(
        ErrorCodes.ConfigurationInvalid,
        "Secret store '" + storeName + "' is read-only.",
        ErrorCategory.Configuration);
}
