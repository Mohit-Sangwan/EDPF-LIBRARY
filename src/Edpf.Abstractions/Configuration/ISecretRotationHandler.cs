using System;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Configuration;

/// <summary>
/// Reacts to a secret being rotated (§10.1 Security). Handlers refresh
/// derived state — connection factories, HTTP clients, signing credentials —
/// without a restart. In-flight work completes on the old value; only new
/// work picks up the new one (Phase 03 §⑧).
/// </summary>
public interface ISecretRotationHandler
{
    /// <summary>The secret key this handler cares about.</summary>
    string SecretKey { get; }

    /// <summary>
    /// Called after the new value is durable and the overlap window is open.
    /// </summary>
    /// <param name="rotation">The incoming and outgoing values.</param>
    /// <param name="cancellationToken">Cancels the refresh.</param>
    /// <returns>
    /// Success once derived state accepts the new value. A failure keeps the
    /// overlap open and raises an alert rather than dropping traffic.
    /// </returns>
    Task<Result> OnRotatedAsync(SecretRotationView rotation, CancellationToken cancellationToken);
}

/// <summary>
/// A rotation that occurred, for auditing. Carries key and timing only —
/// never values (Phase 03 §⑥: audit who, what, when, not the secret).
/// </summary>
public sealed class SecretRotationEvent
{
    /// <summary>
    /// Initializes the event.
    /// </summary>
    /// <param name="secretKey">The rotated key.</param>
    /// <param name="storeName">The store that holds it.</param>
    /// <param name="rotatedUtc">When rotation occurred.</param>
    /// <param name="overlapExpiresUtc">When the dual-secret window closes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="secretKey"/> or <paramref name="storeName"/> is null.</exception>
    public SecretRotationEvent(
        string secretKey,
        string storeName,
        DateTimeOffset rotatedUtc,
        DateTimeOffset? overlapExpiresUtc)
    {
        SecretKey = secretKey ?? throw new ArgumentNullException(nameof(secretKey));
        StoreName = storeName ?? throw new ArgumentNullException(nameof(storeName));
        RotatedUtc = rotatedUtc;
        OverlapExpiresUtc = overlapExpiresUtc;
    }

    /// <summary>The rotated key. Keys are not secret; values are.</summary>
    public string SecretKey { get; }

    /// <summary>The store holding the secret.</summary>
    public string StoreName { get; }

    /// <summary>When the rotation occurred (UTC).</summary>
    public DateTimeOffset RotatedUtc { get; }

    /// <summary>When the dual-secret overlap closes.</summary>
    public DateTimeOffset? OverlapExpiresUtc { get; }
}
