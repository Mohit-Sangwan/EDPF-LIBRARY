using System.Text;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Security;

namespace Edpf.WalkingSkeleton.Tests.Component;

/// <summary>
/// ADR-006/ADR-007 component proofs (Spike-C made permanent): envelope
/// round-trips, key destruction makes data unrecoverable, decrypt failures
/// never leak detail.
/// </summary>
public sealed class CryptoShreddingTests : IDisposable
{
    private readonly SkeletonHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task EncryptDecrypt_SubjectScope_RoundTrips()
    {
        KeyScope scope = KeyScope.ForSubject(_harness.TenantId, Guid.NewGuid());
        byte[] plaintext = Encoding.UTF8.GetBytes("MRN-12345");

        Result<EncryptionEnvelope> envelope =
            await _harness.Crypto.EncryptAsync(plaintext, scope, CancellationToken.None);
        Result<byte[]> decrypted =
            await _harness.Crypto.DecryptAsync(envelope.Value, CancellationToken.None);

        Assert.True(decrypted.IsSuccess);
        Assert.Equal(plaintext, decrypted.Value);
    }

    [Fact]
    public async Task Encrypt_Twice_ProducesDistinctNonces()
    {
        KeyScope scope = KeyScope.ForSubject(_harness.TenantId, Guid.NewGuid());
        byte[] plaintext = Encoding.UTF8.GetBytes("same-plaintext");

        Result<EncryptionEnvelope> first =
            await _harness.Crypto.EncryptAsync(plaintext, scope, CancellationToken.None);
        Result<EncryptionEnvelope> second =
            await _harness.Crypto.EncryptAsync(plaintext, scope, CancellationToken.None);

        Assert.NotEqual(first.Value.Nonce, second.Value.Nonce);
        Assert.NotEqual(first.Value.Ciphertext, second.Value.Ciphertext);
    }

    [Fact]
    public async Task Decrypt_AfterDestroy_FailsWithKeyDestroyed()
    {
        var subjectId = Guid.NewGuid();
        KeyScope scope = KeyScope.ForSubject(_harness.TenantId, subjectId);
        Result<EncryptionEnvelope> envelope = await _harness.Crypto.EncryptAsync(
            Encoding.UTF8.GetBytes("PHI"), scope, CancellationToken.None);

        Result destroyed = await _harness.Kms.DestroyAsync(scope, CancellationToken.None);
        Result<byte[]> decrypted = await _harness.Crypto.DecryptAsync(envelope.Value, CancellationToken.None);

        Assert.True(destroyed.IsSuccess);
        Assert.True(decrypted.IsFailure);
        Assert.Equal(ErrorCodes.KeyDestroyed, decrypted.Error!.Code);
    }

    [Fact]
    public async Task Destroy_OneSubject_LeavesOtherSubjectsReadable()
    {
        var erased = Guid.NewGuid();
        var surviving = Guid.NewGuid();
        Result<EncryptionEnvelope> erasedEnvelope = await _harness.Crypto.EncryptAsync(
            Encoding.UTF8.GetBytes("A"), KeyScope.ForSubject(_harness.TenantId, erased), CancellationToken.None);
        Result<EncryptionEnvelope> survivingEnvelope = await _harness.Crypto.EncryptAsync(
            Encoding.UTF8.GetBytes("B"), KeyScope.ForSubject(_harness.TenantId, surviving), CancellationToken.None);

        await _harness.Kms.DestroyAsync(KeyScope.ForSubject(_harness.TenantId, erased), CancellationToken.None);

        Result<byte[]> erasedRead =
            await _harness.Crypto.DecryptAsync(erasedEnvelope.Value, CancellationToken.None);
        Result<byte[]> survivingRead =
            await _harness.Crypto.DecryptAsync(survivingEnvelope.Value, CancellationToken.None);

        Assert.True(erasedRead.IsFailure);
        Assert.True(survivingRead.IsSuccess);
        Assert.Equal("B", Encoding.UTF8.GetString(survivingRead.Value));
    }

    [Fact]
    public async Task Decrypt_TamperedCiphertext_FailsWithoutDetail()
    {
        KeyScope scope = KeyScope.ForSubject(_harness.TenantId, Guid.NewGuid());
        Result<EncryptionEnvelope> envelope = await _harness.Crypto.EncryptAsync(
            Encoding.UTF8.GetBytes("data"), scope, CancellationToken.None);

        byte[] wire = envelope.Value.Serialize();
        wire[^1] ^= 0xFF; // flip a tag bit
        EncryptionEnvelope tampered = EncryptionEnvelope.Deserialize(wire);

        Result<byte[]> decrypted = await _harness.Crypto.DecryptAsync(tampered, CancellationToken.None);

        Assert.True(decrypted.IsFailure);
        Assert.Equal(ErrorCodes.CryptoFailure, decrypted.Error!.Code);
        Assert.DoesNotContain("tag", decrypted.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Tokenize_SameSubjectSameTenant_IsStable()
    {
        Result<string> first = await _harness.Tokenizer.TokenizeAsync(
            "subject-1", _harness.TenantId, CancellationToken.None);
        Result<string> second = await _harness.Tokenizer.TokenizeAsync(
            "subject-1", _harness.TenantId, CancellationToken.None);

        Assert.Equal(first.Value, second.Value);
    }

    [Fact]
    public async Task Tokenize_SameSubject_DiffersAcrossSubjects()
    {
        Result<string> first = await _harness.Tokenizer.TokenizeAsync(
            "subject-1", _harness.TenantId, CancellationToken.None);
        Result<string> other = await _harness.Tokenizer.TokenizeAsync(
            "subject-2", _harness.TenantId, CancellationToken.None);

        Assert.NotEqual(first.Value, other.Value);
    }
}
