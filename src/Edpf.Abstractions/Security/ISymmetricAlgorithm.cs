using System;

namespace Edpf.Abstractions.Security;

/// <summary>
/// An authenticated symmetric cipher registered with the
/// <see cref="IAlgorithmRegistry"/> (C4 §12.5). Implementations live behind
/// <c>ICryptoProvider</c>; application code never calls a cipher directly
/// (Z.10: crypto uses <c>ICryptoProvider</c>, no direct
/// <c>System.Security.Cryptography</c>).
/// </summary>
public interface ISymmetricAlgorithm
{
    /// <summary>The stable registry id carried in every envelope this algorithm produces.</summary>
    short Id { get; }

    /// <summary>Required key length in bytes.</summary>
    int KeySizeBytes { get; }

    /// <summary>Required nonce length in bytes.</summary>
    int NonceSizeBytes { get; }

    /// <summary>Authentication tag length in bytes.</summary>
    int TagSizeBytes { get; }

    /// <summary>
    /// Encrypts with authentication.
    /// </summary>
    /// <param name="plaintext">The plaintext.</param>
    /// <param name="key">Key of exactly <see cref="KeySizeBytes"/> bytes.</param>
    /// <param name="nonce">Unique nonce of exactly <see cref="NonceSizeBytes"/> bytes. Never reused per key.</param>
    /// <returns>The ciphertext and its authentication tag.</returns>
    (byte[] Ciphertext, byte[] Tag) Encrypt(byte[] plaintext, byte[] key, byte[] nonce);

    /// <summary>
    /// Decrypts and verifies the authentication tag.
    /// </summary>
    /// <param name="ciphertext">The ciphertext.</param>
    /// <param name="tag">The authentication tag produced at encryption.</param>
    /// <param name="key">Key of exactly <see cref="KeySizeBytes"/> bytes.</param>
    /// <param name="nonce">The nonce used at encryption.</param>
    /// <returns>The plaintext.</returns>
    /// <exception cref="System.Security.Cryptography.CryptographicException">Tag verification failed.</exception>
    byte[] Decrypt(byte[] ciphertext, byte[] tag, byte[] key, byte[] nonce);
}
