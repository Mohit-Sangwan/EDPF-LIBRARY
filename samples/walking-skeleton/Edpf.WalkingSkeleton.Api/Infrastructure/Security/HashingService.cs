using System.Security.Cryptography;
using Edpf.Abstractions.Security;

namespace Edpf.WalkingSkeleton.Api.Infrastructure.Security;

/// <summary>SHA-256 / HMAC-SHA256 (C4 §12.3). Stateless and thread-safe.</summary>
public sealed class HashingService : IHashingService
{
    public byte[] Sha256(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return SHA256.HashData(data);
    }

    public byte[] HmacSha256(byte[] key, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(data);
        return HMACSHA256.HashData(key, data);
    }
}
