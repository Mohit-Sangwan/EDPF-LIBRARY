using Edpf.Abstractions.Security;

namespace Edpf.UnitTests.Security;

public sealed class EncryptionEnvelopeTests
{
    private static EncryptionEnvelope Sample(byte[] ciphertext) => new(
        EncryptionEnvelope.CurrentVersion,
        algorithmId: 1,
        keyId: Guid.Parse("11111111-2222-3333-4444-555555555555"),
        keyVersion: 7,
        nonce: new byte[EncryptionEnvelope.NonceSize],
        ciphertext: ciphertext,
        tag: new byte[EncryptionEnvelope.TagSize]);

    [Fact]
    public void SerializeDeserialize_RoundTrip_PreservesAllFields()
    {
        EncryptionEnvelope original = Sample([1, 2, 3, 4, 5]);

        EncryptionEnvelope restored = EncryptionEnvelope.Deserialize(original.Serialize());

        Assert.Equal(original.Version, restored.Version);
        Assert.Equal(original.AlgorithmId, restored.AlgorithmId);
        Assert.Equal(original.KeyId, restored.KeyId);
        Assert.Equal(original.KeyVersion, restored.KeyVersion);
        Assert.Equal(original.Nonce, restored.Nonce);
        Assert.Equal(original.Ciphertext, restored.Ciphertext);
        Assert.Equal(original.Tag, restored.Tag);
    }

    [Fact]
    public void Serialize_FixedHeader_Is35Bytes()
    {
        // The §12.5 wire format: fixed header = 35 bytes (1+2+16+4+12).
        Assert.Equal(35, EncryptionEnvelope.HeaderSize);

        byte[] wire = Sample([]).Serialize();

        Assert.Equal(EncryptionEnvelope.HeaderSize + EncryptionEnvelope.TagSize, wire.Length);
    }

    [Fact]
    public void Serialize_EmptyCiphertext_RoundTrips()
    {
        EncryptionEnvelope restored = EncryptionEnvelope.Deserialize(Sample([]).Serialize());

        Assert.Empty(restored.Ciphertext);
    }

    [Fact]
    public void Deserialize_TooShort_ThrowsFormat()
    {
        Assert.Throws<FormatException>(() => EncryptionEnvelope.Deserialize(new byte[10]));
    }

    [Fact]
    public void Deserialize_UnknownVersion_ThrowsFormat()
    {
        byte[] wire = Sample([1]).Serialize();
        wire[0] = 99;

        Assert.Throws<FormatException>(() => EncryptionEnvelope.Deserialize(wire));
    }

    [Fact]
    public void Deserialize_Null_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => EncryptionEnvelope.Deserialize(null!));
    }

    [Fact]
    public void Constructor_WrongNonceLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => new EncryptionEnvelope(
            1, 1, Guid.NewGuid(), 1, new byte[5], [], new byte[EncryptionEnvelope.TagSize]));
    }

    [Fact]
    public void Constructor_WrongTagLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => new EncryptionEnvelope(
            1, 1, Guid.NewGuid(), 1, new byte[EncryptionEnvelope.NonceSize], [], new byte[3]));
    }

    [Fact]
    public void KeyScope_ForSubject_RequiresBothIds()
    {
        Assert.Throws<ArgumentException>(() => KeyScope.ForSubject(Guid.Empty, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => KeyScope.ForSubject(Guid.NewGuid(), Guid.Empty));
    }

    [Fact]
    public void KeyHandle_Dispose_ZeroesMaterialAndInvalidates()
    {
        var material = new byte[] { 1, 2, 3 };
        var handle = new KeyHandle(Guid.NewGuid(), 1, material);

        handle.Dispose();

        Assert.All(material, b => Assert.Equal(0, b));
        Assert.Throws<ObjectDisposedException>(() => handle.Material);
    }
}
