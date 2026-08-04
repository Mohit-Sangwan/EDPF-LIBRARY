using System.Security.Cryptography;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Security;
using Edpf.Abstractions.Storage;

namespace Edpf.UnitTests.TestDoubles;

/// <summary>
/// Real SHA-256, so a content-hash assertion means something. Test code may
/// touch <c>System.Security.Cryptography</c> directly; framework code may not
/// (Z.10), which is the reason this seam exists at all.
/// </summary>
public sealed class TestHashingService : IHashingService
{
    public byte[] Sha256(byte[] data) => SHA256.HashData(data);

    public byte[] HmacSha256(byte[] key, byte[] data) => HMACSHA256.HashData(key, data);
}

/// <summary>
/// A reversible stand-in for envelope encryption.
/// </summary>
/// <remarks>
/// **This is not cryptography and is not trying to be.** It exists to make one
/// question answerable in a unit test: did the bytes that reached the backend
/// differ from the plaintext, and did the platform hand back the plaintext on
/// the way out. Real AES-GCM has its own test vectors elsewhere; putting it
/// here would test the crypto provider a second time and the storage policy
/// not at all.
/// </remarks>
public sealed class ReversibleTestCryptoProvider : ICryptoProvider
{
    private const byte Mask = 0x5A;

    /// <summary>Key scopes whose material is treated as destroyed (ADR-006).</summary>
    public HashSet<KeyScope> DestroyedScopes { get; } = [];

    /// <summary>The scope of the most recent encrypt call, so tests can assert key binding.</summary>
    public KeyScope? LastScope { get; private set; }

    public Task<Result<EncryptionEnvelope>> EncryptAsync(
        byte[] plaintext,
        KeyScope scope,
        CancellationToken cancellationToken)
    {
        LastScope = scope;

        if (DestroyedScopes.Contains(scope))
        {
            return Task.FromResult(Result.Failure<EncryptionEnvelope>(new Error(
                ErrorCodes.KeyDestroyed, "The key for this scope was destroyed.", ErrorCategory.Security)));
        }

        var ciphertext = new byte[plaintext.Length];
        for (int i = 0; i < plaintext.Length; i++)
        {
            ciphertext[i] = (byte)(plaintext[i] ^ Mask);
        }

        var envelope = new EncryptionEnvelope(
            EncryptionEnvelope.CurrentVersion,
            algorithmId: 1,
            keyId: scope.SubjectId ?? scope.TenantId,
            keyVersion: 1,
            nonce: new byte[EncryptionEnvelope.NonceSize],
            ciphertext: ciphertext,
            tag: new byte[EncryptionEnvelope.TagSize]);

        return Task.FromResult(Result<EncryptionEnvelope>.FromValue(envelope));
    }

    public Task<Result<byte[]>> DecryptAsync(EncryptionEnvelope envelope, CancellationToken cancellationToken)
    {
        var plaintext = new byte[envelope.Ciphertext.Length];
        for (int i = 0; i < plaintext.Length; i++)
        {
            plaintext[i] = (byte)(envelope.Ciphertext[i] ^ Mask);
        }

        return Task.FromResult(Result<byte[]>.FromValue(plaintext));
    }
}

/// <summary>
/// Wraps a backend and fails one operation, so the store's compensating
/// behaviour is observable. A failure path nobody can trigger is a failure
/// path nobody has tested.
/// </summary>
public sealed class FailingMetadataBackend(IBlobBackend inner) : IBlobBackend
{
    public string BackendName => inner.BackendName;

    public Task<Result> PutAsync(BlobPath path, byte[] bytes, CancellationToken cancellationToken)
        => inner.PutAsync(path, bytes, cancellationToken);

    public Task<Result<byte[]>> GetAsync(BlobPath path, CancellationToken cancellationToken)
        => inner.GetAsync(path, cancellationToken);

    public Task<Result> RemoveAsync(BlobPath path, CancellationToken cancellationToken)
        => inner.RemoveAsync(path, cancellationToken);

    public Task<Result<IReadOnlyList<string>>> ListAsync(string renderedPrefix, CancellationToken cancellationToken)
        => inner.ListAsync(renderedPrefix, cancellationToken);

    public Task<Result<IReadOnlyDictionary<string, string>>> GetMetadataAsync(
        BlobPath path,
        CancellationToken cancellationToken)
        => inner.GetMetadataAsync(path, cancellationToken);

    public Task<Result> PutMetadataAsync(
        BlobPath path,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
        => Task.FromResult(Result.Failure(new Error(
            ErrorCodes.ProviderFailure, "Metadata store unavailable.", ErrorCategory.Transient)));
}
