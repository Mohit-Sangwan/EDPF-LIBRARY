using System;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Configuration;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Configuration.Secrets;

/// <summary>
/// Reads secrets from environment variables — the twelve-factor path used by
/// containers and CI, where the orchestrator injects material from its own
/// vault. Read-only by design: a process that can rewrite its own secrets can
/// also be made to leak them.
/// </summary>
/// <remarks>
/// Keys are mapped by uppercasing and replacing <c>:</c> and <c>.</c> with
/// <c>_</c>, so <c>Database:Password</c> reads <c>EDPF_DATABASE_PASSWORD</c>
/// under the default prefix. Rotation is observed rather than performed: the
/// orchestrator restarts or re-injects, and
/// <see cref="GetForRotationAsync"/> reports the previous value from the
/// conventional <c>_PREVIOUS</c> companion variable while it is set.
/// </remarks>
public sealed class EnvironmentSecretStore : ISecretStore
{
    /// <summary>The default environment-variable prefix.</summary>
    public const string DefaultPrefix = "EDPF_";

    /// <summary>Suffix of the companion variable holding the outgoing value during rotation.</summary>
    public const string PreviousSuffix = "_PREVIOUS";

    private readonly string _prefix;
    private readonly Func<string, string?> _read;

    /// <summary>
    /// Initializes the store.
    /// </summary>
    /// <param name="prefix">Environment-variable prefix. Defaults to <see cref="DefaultPrefix"/>.</param>
    /// <param name="reader">
    /// Variable reader; defaults to the process environment. Injectable so
    /// the conformance suite runs without mutating machine state.
    /// </param>
    public EnvironmentSecretStore(string? prefix = null, Func<string, string?>? reader = null)
    {
        _prefix = prefix ?? DefaultPrefix;
        _read = reader ?? Environment.GetEnvironmentVariable;
    }

    /// <inheritdoc />
    public string Name => "environment";

    /// <inheritdoc />
    public Task<Result<SecretValue>> GetAsync(string key, CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(key, nameof(key));
        cancellationToken.ThrowIfCancellationRequested();

        string? value = _read(ToVariableName(key));
        return Task.FromResult(value is null
            ? Result.Failure<SecretValue>(SecretErrors.NotFound(key))
            : Result.Success(new SecretValue(value)));
    }

    /// <inheritdoc />
    public Task<Result<SecretRotationView>> GetForRotationAsync(string key, CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(key, nameof(key));
        cancellationToken.ThrowIfCancellationRequested();

        string variable = ToVariableName(key);
        string? current = _read(variable);
        if (current is null)
        {
            return Task.FromResult(Result.Failure<SecretRotationView>(SecretErrors.NotFound(key)));
        }

        string? previous = _read(variable + PreviousSuffix);

        // The orchestrator owns the window; while the companion variable is
        // set, both values are accepted. No expiry is asserted here because
        // this store cannot know one.
        return Task.FromResult(Result.Success(new SecretRotationView(
            new SecretValue(current),
            previous is null ? null : new SecretValue(previous),
            overlapExpiresUtc: null)));
    }

    /// <inheritdoc />
    public Task<Result> SetAsync(string key, SecretValue value, CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(key, nameof(key));
        Guard.NotNull(value, nameof(value));
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(Result.Failure(SecretErrors.ReadOnly(Name)));
    }

    private string ToVariableName(string key)
        => _prefix + key.Replace(':', '_').Replace('.', '_').ToUpperInvariant();
}
