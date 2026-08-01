using System;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Configuration;

/// <summary>
/// Secret custody (§10.1 Security). Every backend — in-memory, environment,
/// encrypted file, Key Vault, AWS Secrets Manager, Vault, a future HSM —
/// implements this one contract and must pass the identical secret-store
/// conformance suite before it may be used (Phase 03 exit criteria).
/// </summary>
/// <remarks>
/// Every access is audited by the caller as "who, what, when" — never the
/// value (Phase 03 §⑥).
/// </remarks>
public interface ISecretStore
{
    /// <summary>A stable name for the backing store, used in diagnostics. Never contains credentials.</summary>
    string Name { get; }

    /// <summary>
    /// Reads the current value of a secret.
    /// </summary>
    /// <param name="key">The secret's key.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The secret, or failure with <see cref="ErrorCodes.ConfigurationInvalid"/>
    /// when it does not exist. The failure never echoes the value, and the
    /// message names the key only.
    /// </returns>
    Task<Result<SecretValue>> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a secret during a rotation overlap window, returning both the
    /// incoming and the outgoing value so callers can accept either
    /// (Phase 03 §④ dual-secret window).
    /// </summary>
    /// <param name="key">The secret's key.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The current value, plus the previous value while an overlap is open.</returns>
    Task<Result<SecretRotationView>> GetForRotationAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Stores or replaces a secret, opening a rotation overlap when a previous
    /// value exists.
    /// </summary>
    /// <param name="key">The secret's key.</param>
    /// <param name="value">The new value.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Success, or failure when the store is read-only.</returns>
    Task<Result> SetAsync(string key, SecretValue value, CancellationToken cancellationToken);
}

/// <summary>
/// A secret as seen during rotation: the incoming value, and the outgoing one
/// while its overlap window is still open. Accepting both is what makes
/// credential rotation possible without downtime.
/// </summary>
public sealed class SecretRotationView
{
    /// <summary>
    /// Initializes the view.
    /// </summary>
    /// <param name="current">The current (incoming) value.</param>
    /// <param name="previous">The outgoing value while its overlap is open; null otherwise.</param>
    /// <param name="overlapExpiresUtc">When the overlap closes; null when no overlap is open.</param>
    /// <exception cref="ArgumentNullException"><paramref name="current"/> is null.</exception>
    public SecretRotationView(SecretValue current, SecretValue? previous, DateTimeOffset? overlapExpiresUtc)
    {
        Current = current ?? throw new ArgumentNullException(nameof(current));
        Previous = previous;
        OverlapExpiresUtc = overlapExpiresUtc;
    }

    /// <summary>The current (incoming) value.</summary>
    public SecretValue Current { get; }

    /// <summary>The outgoing value while its overlap window is open; null otherwise.</summary>
    public SecretValue? Previous { get; }

    /// <summary>When the overlap window closes; null when none is open.</summary>
    public DateTimeOffset? OverlapExpiresUtc { get; }

    /// <summary>True while both values must be accepted.</summary>
    public bool IsRotating => Previous is not null;
}
