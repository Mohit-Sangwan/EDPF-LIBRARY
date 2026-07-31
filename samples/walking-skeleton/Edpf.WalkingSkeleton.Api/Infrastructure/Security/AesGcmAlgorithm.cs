using System.Security.Cryptography;
using Edpf.Abstractions.Security;

namespace Edpf.WalkingSkeleton.Api.Infrastructure.Security;

/// <summary>
/// AES-256-GCM, algorithm id 1 (C4 §12.5). The first entry in the ADR-007
/// registry; a PQC successor registers under a new id with no data migration.
/// </summary>
public sealed class AesGcmAlgorithm : ISymmetricAlgorithm
{
    /// <summary>The stable registry id of AES-256-GCM.</summary>
    public const short AlgorithmId = 1;

    public short Id => AlgorithmId;

    public int KeySizeBytes => 32;

    public int NonceSizeBytes => EncryptionEnvelope.NonceSize;

    public int TagSizeBytes => EncryptionEnvelope.TagSize;

    public (byte[] Ciphertext, byte[] Tag) Encrypt(byte[] plaintext, byte[] key, byte[] nonce)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];
        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return (ciphertext, tag);
    }

    public byte[] Decrypt(byte[] ciphertext, byte[] tag, byte[] key, byte[] nonce)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(tag);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(nonce);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
