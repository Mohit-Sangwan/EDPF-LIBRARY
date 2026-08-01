using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Configuration;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Configuration.Secrets;

/// <summary>
/// Layers several stores in precedence order — the mechanism that lets a
/// deployment read most secrets from a cloud vault while overriding a few
/// from the environment, without any component knowing which store answered.
/// </summary>
/// <remarks>
/// The first store that has the key wins; a store that lacks it is not a
/// failure, it is a miss. Writes go to the first writable store, so a
/// read-only layer (environment) never silently swallows a rotation.
/// </remarks>
public sealed class ChainedSecretStore : ISecretStore
{
    private readonly List<ISecretStore> _stores;

    /// <summary>
    /// Initializes the chain.
    /// </summary>
    /// <param name="stores">Stores in precedence order, highest first.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stores"/> is null.</exception>
    /// <exception cref="ArgumentException">The chain is empty.</exception>
    public ChainedSecretStore(IEnumerable<ISecretStore> stores)
    {
        Guard.NotNull(stores, nameof(stores));
        _stores = stores.ToList();

        if (_stores.Count == 0)
        {
            throw new ArgumentException("A chained secret store requires at least one store.", nameof(stores));
        }

        Name = "chained[" + string.Join(" > ", _stores.Select(s => s.Name)) + "]";
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public async Task<Result<SecretValue>> GetAsync(string key, CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(key, nameof(key));

        foreach (ISecretStore store in _stores)
        {
            Result<SecretValue> result = await store.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                return result;
            }
        }

        return Result.Failure<SecretValue>(SecretErrors.NotFound(key));
    }

    /// <inheritdoc />
    public async Task<Result<SecretRotationView>> GetForRotationAsync(
        string key, CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(key, nameof(key));

        foreach (ISecretStore store in _stores)
        {
            Result<SecretRotationView> result =
                await store.GetForRotationAsync(key, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                return result;
            }
        }

        return Result.Failure<SecretRotationView>(SecretErrors.NotFound(key));
    }

    /// <inheritdoc />
    public async Task<Result> SetAsync(string key, SecretValue value, CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(key, nameof(key));
        Guard.NotNull(value, nameof(value));

        foreach (ISecretStore store in _stores)
        {
            Result result = await store.SetAsync(key, value, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                return result;
            }
        }

        return Result.Failure(SecretErrors.ReadOnly(Name));
    }
}
