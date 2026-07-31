using System.Security.Cryptography;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Security;
using Edpf.WalkingSkeleton.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Edpf.WalkingSkeleton.Api.Infrastructure.Security;

/// <summary>Skeleton key-custody options.</summary>
public sealed class KeyManagementOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "KeyManagement";

    /// <summary>
    /// Base64 master key wrapping tenant KEKs. Dev harness only — production
    /// custody moves to an HSM/KMS-backed ISecretStore in Phase 03/20. When
    /// unset, an ephemeral master key is generated per process (Development).
    /// </summary>
    public string? MasterKeyBase64 { get; set; }
}

/// <summary>
/// EF-backed key custody for the skeleton (ADR-006/ADR-007; C4 §12.5).
/// Hierarchy: master key → tenant KEK → DEK (subject- or tenant-scoped).
/// Keys rest only wrapped (as serialized envelopes); <see cref="DestroyAsync"/>
/// zeroes and tombstones the row — the crypto-shredding erasure primitive.
/// </summary>
public sealed class KeyManagementService : IKeyManagementService
{
    private readonly SkeletonDbContext _db;
    private readonly IAlgorithmRegistry _registry;
    private readonly IClock _clock;
    private readonly byte[] _masterKey;

    public KeyManagementService(
        SkeletonDbContext db,
        IAlgorithmRegistry registry,
        IClock clock,
        IOptions<KeyManagementOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _db = db;
        _registry = registry;
        _clock = clock;
        _masterKey = MasterKey.Resolve(options.Value);
    }

    public async Task<Result<KeyHandle>> GetCurrentAsync(KeyScope scope, CancellationToken cancellationToken)
    {
        KeyRow? row = await _db.Keys
            .Where(k => k.TenantId == scope.TenantId
                && k.SubjectId == scope.SubjectId
                && k.Purpose == KeyPurpose.DataEncryption)
            .OrderByDescending(k => k.KeyVersion)
            .FirstOrDefaultAsync(cancellationToken);

        if (row is not null && row.DestroyedUtc is not null)
        {
            return KeyDestroyedError(row.DestroyedUtc.Value);
        }

        if (row is null)
        {
            row = await CreateKeyAsync(scope, KeyPurpose.DataEncryption, cancellationToken);
        }

        return Unwrap(row);
    }

    public async Task<Result<KeyHandle>> ResolveAsync(Guid keyId, int keyVersion, CancellationToken cancellationToken)
    {
        KeyRow? row = await _db.Keys
            .FirstOrDefaultAsync(k => k.KeyId == keyId && k.KeyVersion == keyVersion, cancellationToken);

        if (row is null)
        {
            return Result.Failure<KeyHandle>(new Error(
                ErrorCodes.CryptoFailure, "Envelope references an unknown key.", ErrorCategory.Security));
        }

        if (row.DestroyedUtc is not null)
        {
            return KeyDestroyedError(row.DestroyedUtc.Value);
        }

        return Unwrap(row);
    }

    public async Task<Result> DestroyAsync(KeyScope scope, CancellationToken cancellationToken)
    {
        List<KeyRow> rows = await _db.Keys
            .Where(k => k.TenantId == scope.TenantId
                && k.SubjectId == scope.SubjectId
                && k.Purpose == KeyPurpose.DataEncryption
                && k.DestroyedUtc == null)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return Result.Failure(new Error(
                ErrorCodes.NotFound, "No live keys exist for the scope.", ErrorCategory.NotFound));
        }

        DateTimeOffset now = _clock.UtcNow;
        foreach (KeyRow row in rows)
        {
            // Zero the wrapped material AND tombstone the row: even a leaked
            // KEK cannot resurrect the DEK after this commit (ADR-006).
            row.WrappedKey = new byte[row.WrappedKey.Length];
            row.DestroyedUtc = now;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>
    /// Resolves the tenant-scoped audit salt used for subject tokenization
    /// (C4 §12.3) — held as its own destroyable key so the token→identity
    /// mapping is itself erasable.
    /// </summary>
    public async Task<Result<KeyHandle>> GetAuditSaltAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        KeyRow? row = await _db.Keys
            .Where(k => k.TenantId == tenantId && k.SubjectId == null && k.Purpose == KeyPurpose.AuditSalt)
            .OrderByDescending(k => k.KeyVersion)
            .FirstOrDefaultAsync(cancellationToken);

        row ??= await CreateKeyAsync(KeyScope.ForTenant(tenantId), KeyPurpose.AuditSalt, cancellationToken);

        if (row.DestroyedUtc is not null)
        {
            return KeyDestroyedError(row.DestroyedUtc.Value);
        }

        return Unwrap(row);
    }

    private async Task<KeyRow> CreateKeyAsync(KeyScope scope, KeyPurpose purpose, CancellationToken cancellationToken)
    {
        byte[] kek = await GetOrCreateTenantKekAsync(scope.TenantId, cancellationToken);
        try
        {
            byte[] dek = RandomNumberGenerator.GetBytes(32);
            try
            {
                var row = new KeyRow
                {
                    KeyId = Guid.NewGuid(),
                    TenantId = scope.TenantId,
                    SubjectId = scope.SubjectId,
                    Purpose = purpose,
                    KeyVersion = 1,
                    WrappedKey = Wrap(dek, kek),
                    CreatedUtc = _clock.UtcNow,
                };
                _db.Keys.Add(row);
                await _db.SaveChangesAsync(cancellationToken);
                return row;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dek);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private async Task<byte[]> GetOrCreateTenantKekAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        KeyRow? row = await _db.Keys
            .Where(k => k.TenantId == tenantId && k.SubjectId == null && k.Purpose == KeyPurpose.KeyEncryption)
            .OrderByDescending(k => k.KeyVersion)
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            byte[] kek = RandomNumberGenerator.GetBytes(32);
            try
            {
                row = new KeyRow
                {
                    KeyId = Guid.NewGuid(),
                    TenantId = tenantId,
                    SubjectId = null,
                    Purpose = KeyPurpose.KeyEncryption,
                    KeyVersion = 1,
                    WrappedKey = Wrap(kek, _masterKey),
                    CreatedUtc = _clock.UtcNow,
                };
                _db.Keys.Add(row);
                await _db.SaveChangesAsync(cancellationToken);
                return (byte[])kek.Clone();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }

        return UnwrapRaw(row.WrappedKey, _masterKey);
    }

    private Result<KeyHandle> Unwrap(KeyRow row)
    {
        byte[] kek = UnwrapTenantKek(row.TenantId);
        try
        {
            byte[] material = UnwrapRaw(row.WrappedKey, kek);
            return Result.Success(new KeyHandle(row.KeyId, row.KeyVersion, material));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private byte[] UnwrapTenantKek(Guid tenantId)
    {
        KeyRow kekRow = _db.Keys
            .Where(k => k.TenantId == tenantId && k.SubjectId == null && k.Purpose == KeyPurpose.KeyEncryption)
            .OrderByDescending(k => k.KeyVersion)
            .First();
        return UnwrapRaw(kekRow.WrappedKey, _masterKey);
    }

    private byte[] Wrap(byte[] material, byte[] wrappingKey)
    {
        ISymmetricAlgorithm algorithm = _registry.Current;
        byte[] nonce = RandomNumberGenerator.GetBytes(algorithm.NonceSizeBytes);
        (byte[] ciphertext, byte[] tag) = algorithm.Encrypt(material, wrappingKey, nonce);
        var envelope = new EncryptionEnvelope(
            EncryptionEnvelope.CurrentVersion, algorithm.Id, Guid.Empty, 0, nonce, ciphertext, tag);
        return envelope.Serialize();
    }

    private byte[] UnwrapRaw(byte[] wrapped, byte[] wrappingKey)
    {
        EncryptionEnvelope envelope = EncryptionEnvelope.Deserialize(wrapped);
        Result<ISymmetricAlgorithm> algorithm = _registry.Resolve(envelope.AlgorithmId);
        return algorithm.Value.Decrypt(envelope.Ciphertext, envelope.Tag, wrappingKey, envelope.Nonce);
    }

    private static Result<KeyHandle> KeyDestroyedError(DateTimeOffset destroyedUtc)
        => Result.Failure<KeyHandle>(new Error(
            ErrorCodes.KeyDestroyed,
            $"Key material was destroyed on {destroyedUtc:yyyy-MM-dd} (crypto-shredding erasure).",
            ErrorCategory.Security));
}

/// <summary>Master-key resolution for the dev harness.</summary>
internal static class MasterKey
{
    private static readonly Lazy<byte[]> Ephemeral = new(() => RandomNumberGenerator.GetBytes(32));

    internal static byte[] Resolve(KeyManagementOptions options)
        => string.IsNullOrEmpty(options.MasterKeyBase64)
            ? Ephemeral.Value
            : Convert.FromBase64String(options.MasterKeyBase64);
}
