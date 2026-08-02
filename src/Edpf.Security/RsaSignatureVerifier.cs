using System;
using System.Security.Cryptography;
using Edpf.Abstractions.Security;
using Edpf.Core.Guards;

namespace Edpf.Security;

/// <summary>
/// Verifies detached RSA signatures over entitlement payloads (Phase 34b).
/// </summary>
/// <remarks>
/// <para>
/// Lives here because Z.10 confines cryptography to this assembly. That rule
/// exists so crypto sits in one reviewed place — not so it sits nowhere — and
/// the licensing package consumes
/// <see cref="IDetachedSignatureVerifier"/> rather than earning a second
/// exemption.
/// </para>
/// <para>
/// **RSASSA-PSS with SHA-256**, not PKCS#1 v1.5. PSS has a security proof and
/// v1.5 does not; both are widely deployed and there is no compatibility
/// reason to pick the weaker one for a format being defined here rather than
/// interoperated with.
/// </para>
/// <para>
/// **This type holds a public key only.** The private key that issues
/// entitlements belongs to whoever sells the software; a deployment that could
/// sign would not need a licence.
/// </para>
/// </remarks>
public sealed class RsaSignatureVerifier : IDetachedSignatureVerifier, IDisposable
{
    private readonly RSA _publicKey;

    /// <summary>
    /// Initializes a verifier from a SubjectPublicKeyInfo DER blob.
    /// </summary>
    /// <param name="subjectPublicKeyInfo">The public key.</param>
    /// <exception cref="ArgumentException">The key is not a usable public key.</exception>
    public RsaSignatureVerifier(byte[] subjectPublicKeyInfo)
    {
        Guard.NotNull(subjectPublicKeyInfo, nameof(subjectPublicKeyInfo));

        _publicKey = RSA.Create();

        try
        {
            _publicKey.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
        }
        catch (CryptographicException ex)
        {
            _publicKey.Dispose();

            // The key material itself is not echoed. A malformed key is a
            // deployment error, and its bytes are of no use in the message.
            throw new ArgumentException(
                "The supplied bytes are not a valid RSA SubjectPublicKeyInfo.",
                nameof(subjectPublicKeyInfo),
                ex);
        }

        if (_publicKey.KeySize < MinimumKeySizeBits)
        {
            int size = _publicKey.KeySize;
            _publicKey.Dispose();

            throw new ArgumentException(
                $"An entitlement signing key must be at least {MinimumKeySizeBits} bits; this one is "
                + $"{size}.",
                nameof(subjectPublicKeyInfo));
        }
    }

    /// <summary>
    /// The smallest accepted RSA key.
    /// </summary>
    /// <remarks>
    /// 2048 is the current floor in NIST SP 800-57 and everywhere else that
    /// publishes one. Enforced at construction rather than trusted, because a
    /// deployment that quietly downgrades to a 1024-bit key gets a system that
    /// still verifies signatures and no longer means anything by it.
    /// </remarks>
    public const int MinimumKeySizeBits = 2048;

    /// <inheritdoc />
    public bool Verify(byte[] payload, byte[] signature)
    {
        if (payload is null || signature is null || signature.Length == 0)
        {
            return false;
        }

        try
        {
            return _publicKey.VerifyData(
                payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (CryptographicException)
        {
            // A malformed signature is an expected input — a truncated file, a
            // licence for a different product — and reports as invalid rather
            // than as a fault.
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _publicKey.Dispose();
}
