using System.Security.Cryptography;

namespace Edpf.WalkingSkeleton.Api.Infrastructure.Auth;

/// <summary>
/// JWT settings for the skeleton's symmetric-key harness. Phase 21 replaces
/// this with OIDC against a real identity provider; the seam is the bearer
/// scheme, not this class.
/// </summary>
public sealed class JwtOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "edpf-walking-skeleton";

    public string Audience { get; set; } = "edpf-api";

    /// <summary>
    /// Base64 signing key. Unset in Development → an ephemeral per-process key
    /// (tokens die with the process; nothing secret is ever committed).
    /// Unset in Production → startup fails with EDPF-CFG-8001; supply it via
    /// environment or secret store, never a config file.
    /// </summary>
    public string? SigningKeyBase64 { get; set; }

    private static readonly Lazy<byte[]> EphemeralKey = new(() => RandomNumberGenerator.GetBytes(64));

    /// <summary>Resolves the signing key bytes (configured or ephemeral).</summary>
    public byte[] ResolveSigningKey()
        => string.IsNullOrEmpty(SigningKeyBase64)
            ? EphemeralKey.Value
            : Convert.FromBase64String(SigningKeyBase64);
}
