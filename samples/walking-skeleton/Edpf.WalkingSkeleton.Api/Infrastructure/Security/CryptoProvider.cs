using System.Security.Cryptography;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Security;

namespace Edpf.WalkingSkeleton.Api.Infrastructure.Security;

/// <summary>
/// Envelope encryption per C4 §12.5: encrypt always uses the registry's
/// current algorithm; decrypt always honours what the envelope declares.
/// A destroyed key surfaces as <see cref="ErrorCodes.KeyDestroyed"/> so the
/// caller renders a tombstone (ADR-006) — never an exception that leaks
/// whether data existed.
/// </summary>
public sealed class CryptoProvider(IAlgorithmRegistry registry, IKeyManagementService kms) : ICryptoProvider
{
    public async Task<Result<EncryptionEnvelope>> EncryptAsync(
        byte[] plaintext, KeyScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        Result<KeyHandle> key = await kms.GetCurrentAsync(scope, cancellationToken);
        if (key.IsFailure)
        {
            return Result.Failure<EncryptionEnvelope>(key.Error!);
        }

        using KeyHandle handle = key.Value;
        ISymmetricAlgorithm algorithm = registry.Current;
        byte[] nonce = RandomNumberGenerator.GetBytes(algorithm.NonceSizeBytes);
        (byte[] ciphertext, byte[] tag) = algorithm.Encrypt(plaintext, handle.Material, nonce);

        return Result.Success(new EncryptionEnvelope(
            EncryptionEnvelope.CurrentVersion,
            algorithm.Id,
            handle.KeyId,
            handle.KeyVersion,
            nonce,
            ciphertext,
            tag));
    }

    public async Task<Result<byte[]>> DecryptAsync(
        EncryptionEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        Result<ISymmetricAlgorithm> algorithm = registry.Resolve(envelope.AlgorithmId);
        if (algorithm.IsFailure)
        {
            return Result.Failure<byte[]>(algorithm.Error!);
        }

        Result<KeyHandle> key = await kms.ResolveAsync(envelope.KeyId, envelope.KeyVersion, cancellationToken);
        if (key.IsFailure)
        {
            return Result.Failure<byte[]>(key.Error!);
        }

        using KeyHandle handle = key.Value;
        try
        {
            return Result.Success(algorithm.Value.Decrypt(
                envelope.Ciphertext, envelope.Tag, handle.Material, envelope.Nonce));
        }
        catch (CryptographicException)
        {
            // Tag mismatch: tampered ciphertext or wrong key. Detail stays in
            // the log against the correlation id — never in the error.
            return Result.Failure<byte[]>(new Error(
                ErrorCodes.CryptoFailure, "Decryption failed.", ErrorCategory.Security));
        }
    }
}
