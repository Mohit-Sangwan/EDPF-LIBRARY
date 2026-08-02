using System;

namespace Edpf.Abstractions.Security;

/// <summary>
/// Verifies a detached signature over a payload (Phase 34b).
/// </summary>
/// <remarks>
/// <para>
/// Exists so that offline entitlement validation can check a signature
/// without importing <c>System.Security.Cryptography</c>. Z.10 confines
/// cryptography to <c>Edpf.Security</c>, and that rule is worth more than the
/// convenience of a direct call — so licensing consumes this seam and the
/// implementation stays in the one reviewed place.
/// </para>
/// <para>
/// **Verification only — there is no signing counterpart here.** The private
/// key that issues entitlements belongs to whoever sells the software, not to
/// the deployment running it. An interface offering both operations would
/// invite a symmetric implementation, and a shared secret sitting on a
/// customer's air-gapped server is a licence-minting kit.
/// </para>
/// </remarks>
public interface IDetachedSignatureVerifier
{
    /// <summary>
    /// Verifies that <paramref name="signature"/> covers <paramref name="payload"/>.
    /// </summary>
    /// <param name="payload">The signed bytes.</param>
    /// <param name="signature">The detached signature.</param>
    /// <returns>Whether the signature is valid for this verifier's key.</returns>
    /// <remarks>
    /// <para>
    /// Returns a boolean rather than throwing, because an invalid signature is
    /// an expected input — a corrupted file, a licence for a different
    /// product, an attempt at forgery — and not an exceptional condition.
    /// </para>
    /// <para>
    /// <c>byte[]</c> rather than <c>ReadOnlySpan&lt;byte&gt;</c>: spans need
    /// <c>System.Memory</c> on Tier 3, and this assembly carries no package
    /// references at all (EDPF0001). A polyfill here would put a dependency in
    /// the one place the architecture says must have none, so the allocation
    /// is accepted instead — an entitlement is verified at startup, not in a
    /// loop (ADR-002).
    /// </para>
    /// </remarks>
    bool Verify(byte[] payload, byte[] signature);
}
