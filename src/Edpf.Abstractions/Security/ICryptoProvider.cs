using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Security;

/// <summary>
/// The single entry point for envelope encryption (§10.1 Security; C4 §12.5).
/// All EDPF cryptography flows through this seam — application and framework
/// code never touches <c>System.Security.Cryptography</c> directly (Z.10),
/// which is what makes algorithm agility (ADR-007) enforceable.
/// </summary>
public interface ICryptoProvider
{
    /// <summary>
    /// Encrypts under the current algorithm with a key resolved for
    /// <paramref name="scope"/>.
    /// </summary>
    /// <param name="plaintext">The plaintext bytes.</param>
    /// <param name="scope">Whose key to use (tenant- or subject-scoped).</param>
    /// <param name="cancellationToken">Cancels key resolution.</param>
    /// <returns>
    /// The self-describing envelope, or failure with
    /// <see cref="ErrorCodes.CryptoFailure"/> /
    /// <see cref="ErrorCodes.KeyDestroyed"/> (a shredded subject cannot receive
    /// new data under the destroyed key).
    /// </returns>
    Task<Result<EncryptionEnvelope>> EncryptAsync(
        byte[] plaintext,
        KeyScope scope,
        CancellationToken cancellationToken);

    /// <summary>
    /// Decrypts an envelope, resolving exactly the algorithm and key version it
    /// declares.
    /// </summary>
    /// <param name="envelope">The envelope to decrypt.</param>
    /// <param name="cancellationToken">Cancels key resolution.</param>
    /// <returns>
    /// The plaintext, or failure with <see cref="ErrorCodes.KeyDestroyed"/>
    /// when the key was crypto-shredded (ADR-006) — the caller renders a
    /// tombstone, never an exception that leaks whether data existed.
    /// </returns>
    Task<Result<byte[]>> DecryptAsync(
        EncryptionEnvelope envelope,
        CancellationToken cancellationToken);
}
