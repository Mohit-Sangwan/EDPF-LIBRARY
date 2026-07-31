using System.Text;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Security;

namespace Edpf.WalkingSkeleton.Api.Infrastructure.Security;

/// <summary>
/// HMAC-SHA256 subject tokenization, tenant-salted (C4 §12.3). The salt is a
/// destroyable tenant key, so the token→identity mapping is itself erasable
/// (ADR-006). Audit records and events carry these tokens, never identifiers.
/// </summary>
public sealed class SubjectTokenizer(KeyManagementService kms, IHashingService hashing) : ITokenizer
{
    public async Task<Result<string>> TokenizeAsync(
        string rawIdentifier, Guid tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(rawIdentifier);

        Result<KeyHandle> salt = await kms.GetAuditSaltAsync(tenantId, cancellationToken);
        if (salt.IsFailure)
        {
            return Result.Failure<string>(salt.Error!);
        }

        using KeyHandle handle = salt.Value;
        byte[] mac = hashing.HmacSha256(handle.Material, Encoding.UTF8.GetBytes(rawIdentifier));
        return Result.Success(Convert.ToBase64String(mac));
    }
}
