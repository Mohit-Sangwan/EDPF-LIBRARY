using System;

namespace Edpf.Abstractions.Security;

/// <summary>
/// The self-describing ciphertext container of ADR-007 (C4 §12.5). Every
/// ciphertext declares its own algorithm, key id and key version, so algorithm
/// migration — including post-quantum — is a registry entry plus configuration,
/// never a schema change or a data migration.
/// </summary>
/// <remarks>
/// <para>Wire format (little-endian, fixed 35-byte header):</para>
/// <code>
/// ┌────────┬─────────────┬──────────┬─────────────┬────────┬────────────┬───────┐
/// │ Ver(1) │ AlgorithmId │ KeyId    │ KeyVersion  │ Nonce  │ Ciphertext │ Tag   │
/// │ byte   │ (2) int16   │ (16) guid│ (4) int32   │ (12)   │ (n)        │ (16)  │
/// └────────┴─────────────┴──────────┴─────────────┴────────┴────────────┴───────┘
/// </code>
/// <para>
/// The <c>KeyId</c> block uses <see cref="Guid.ToByteArray()"/> ordering, which
/// is platform-stable. Buffer properties expose the underlying arrays without
/// defensive copies by design — the envelope sits on the per-field crypto hot
/// path and its allocation budget is part of the Z.18 benchmark set; callers
/// must treat the buffers as immutable.
/// </para>
/// </remarks>
public sealed class EncryptionEnvelope
{
    /// <summary>The current wire-format version.</summary>
    public const byte CurrentVersion = 1;

    /// <summary>Nonce length in bytes (96-bit, AES-GCM standard).</summary>
    public const int NonceSize = 12;

    /// <summary>Authentication tag length in bytes (128-bit).</summary>
    public const int TagSize = 16;

    /// <summary>Fixed header length in bytes: version + algorithm + key id + key version.</summary>
    public const int HeaderSize = 1 + 2 + 16 + 4 + NonceSize;

    /// <summary>
    /// Initializes an envelope from its parts.
    /// </summary>
    /// <param name="version">Wire-format version.</param>
    /// <param name="algorithmId">Registry id of the algorithm that produced the ciphertext.</param>
    /// <param name="keyId">Id of the data-encryption key.</param>
    /// <param name="keyVersion">Version of the data-encryption key.</param>
    /// <param name="nonce">The nonce; exactly <see cref="NonceSize"/> bytes.</param>
    /// <param name="ciphertext">The ciphertext bytes.</param>
    /// <param name="tag">The authentication tag; exactly <see cref="TagSize"/> bytes.</param>
    /// <exception cref="ArgumentNullException">Any buffer is null.</exception>
    /// <exception cref="ArgumentException">Nonce or tag has the wrong length.</exception>
    public EncryptionEnvelope(
        byte version,
        short algorithmId,
        Guid keyId,
        int keyVersion,
        byte[] nonce,
        byte[] ciphertext,
        byte[] tag)
    {
        if (nonce is null)
        {
            throw new ArgumentNullException(nameof(nonce));
        }

        if (ciphertext is null)
        {
            throw new ArgumentNullException(nameof(ciphertext));
        }

        if (tag is null)
        {
            throw new ArgumentNullException(nameof(tag));
        }

        if (nonce.Length != NonceSize)
        {
            throw new ArgumentException("Nonce must be exactly " + NonceSize + " bytes.", nameof(nonce));
        }

        if (tag.Length != TagSize)
        {
            throw new ArgumentException("Tag must be exactly " + TagSize + " bytes.", nameof(tag));
        }

        Version = version;
        AlgorithmId = algorithmId;
        KeyId = keyId;
        KeyVersion = keyVersion;
        Nonce = nonce;
        Ciphertext = ciphertext;
        Tag = tag;
    }

    /// <summary>Wire-format version of this envelope.</summary>
    public byte Version { get; }

    /// <summary>Registry id of the algorithm that produced the ciphertext.</summary>
    public short AlgorithmId { get; }

    /// <summary>Id of the data-encryption key. Resolution honours destroyed keys (ADR-006).</summary>
    public Guid KeyId { get; }

    /// <summary>Version of the data-encryption key (rotation without re-encryption).</summary>
    public int KeyVersion { get; }

    /// <summary>The nonce. Treat as immutable.</summary>
    public byte[] Nonce { get; }

    /// <summary>The ciphertext. Treat as immutable.</summary>
    public byte[] Ciphertext { get; }

    /// <summary>The authentication tag. Treat as immutable.</summary>
    public byte[] Tag { get; }

    /// <summary>
    /// Serializes to the wire format described in the type remarks.
    /// </summary>
    /// <returns>A new buffer containing the serialized envelope.</returns>
    public byte[] Serialize()
    {
        var buffer = new byte[HeaderSize + Ciphertext.Length + TagSize];
        int offset = 0;

        buffer[offset] = Version;
        offset += 1;

        buffer[offset] = (byte)AlgorithmId;
        buffer[offset + 1] = (byte)((ushort)AlgorithmId >> 8);
        offset += 2;

        byte[] keyIdBytes = KeyId.ToByteArray();
        Array.Copy(keyIdBytes, 0, buffer, offset, 16);
        offset += 16;

        buffer[offset] = (byte)KeyVersion;
        buffer[offset + 1] = (byte)((uint)KeyVersion >> 8);
        buffer[offset + 2] = (byte)((uint)KeyVersion >> 16);
        buffer[offset + 3] = (byte)((uint)KeyVersion >> 24);
        offset += 4;

        Array.Copy(Nonce, 0, buffer, offset, NonceSize);
        offset += NonceSize;

        Array.Copy(Ciphertext, 0, buffer, offset, Ciphertext.Length);
        offset += Ciphertext.Length;

        Array.Copy(Tag, 0, buffer, offset, TagSize);

        return buffer;
    }

    /// <summary>
    /// Deserializes an envelope from its wire format.
    /// </summary>
    /// <param name="raw">The serialized envelope.</param>
    /// <exception cref="ArgumentNullException"><paramref name="raw"/> is null.</exception>
    /// <exception cref="FormatException">
    /// The buffer is shorter than the fixed header + tag, or declares an
    /// unknown wire version. Message contains structural detail only — never
    /// key material.
    /// </exception>
    public static EncryptionEnvelope Deserialize(byte[] raw)
    {
        if (raw is null)
        {
            throw new ArgumentNullException(nameof(raw));
        }

        if (raw.Length < HeaderSize + TagSize)
        {
            throw new FormatException(
                "Envelope buffer too short: " + raw.Length + " bytes; minimum is "
                + (HeaderSize + TagSize) + ".");
        }

        int offset = 0;

        byte version = raw[offset];
        offset += 1;

        if (version != CurrentVersion)
        {
            throw new FormatException("Unknown envelope wire version " + version + ".");
        }

        short algorithmId = (short)(raw[offset] | (raw[offset + 1] << 8));
        offset += 2;

        var keyIdBytes = new byte[16];
        Array.Copy(raw, offset, keyIdBytes, 0, 16);
        var keyId = new Guid(keyIdBytes);
        offset += 16;

        int keyVersion = raw[offset] | (raw[offset + 1] << 8) | (raw[offset + 2] << 16) | (raw[offset + 3] << 24);
        offset += 4;

        var nonce = new byte[NonceSize];
        Array.Copy(raw, offset, nonce, 0, NonceSize);
        offset += NonceSize;

        int ciphertextLength = raw.Length - offset - TagSize;
        var ciphertext = new byte[ciphertextLength];
        Array.Copy(raw, offset, ciphertext, 0, ciphertextLength);
        offset += ciphertextLength;

        var tag = new byte[TagSize];
        Array.Copy(raw, offset, tag, 0, TagSize);

        return new EncryptionEnvelope(version, algorithmId, keyId, keyVersion, nonce, ciphertext, tag);
    }
}
