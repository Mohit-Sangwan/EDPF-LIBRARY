namespace Edpf.Abstractions.Security;

/// <summary>
/// Hashing primitives used by the audit chain and subject tokenization
/// (§10.1 Security; C4 §12.3). Kept behind a seam so algorithm upgrades follow
/// ADR-007 governance rather than ad-hoc call-site edits.
/// </summary>
public interface IHashingService
{
    /// <summary>
    /// SHA-256 digest of <paramref name="data"/>.
    /// </summary>
    /// <param name="data">The bytes to hash.</param>
    /// <returns>The 32-byte digest.</returns>
    byte[] Sha256(byte[] data);

    /// <summary>
    /// HMAC-SHA256 of <paramref name="data"/> under <paramref name="key"/> —
    /// the subject-token primitive (tenant-salted, per C4 §12.3).
    /// </summary>
    /// <param name="key">The HMAC key.</param>
    /// <param name="data">The bytes to authenticate.</param>
    /// <returns>The 32-byte MAC.</returns>
    byte[] HmacSha256(byte[] key, byte[] data);
}
