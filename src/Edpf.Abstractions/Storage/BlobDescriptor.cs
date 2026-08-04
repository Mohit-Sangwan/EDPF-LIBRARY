using System;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Storage;

/// <summary>
/// What the platform knows about a stored blob. Metadata only — never content.
/// </summary>
/// <remarks>
/// <para>
/// The distinction between <see cref="DeclaredContentType"/> and
/// <see cref="ServedContentType"/> is the whole point of the type. The first is
/// what the uploader said; the second is what the platform is willing to put in
/// a <c>Content-Type</c> header. They differ whenever honouring the caller
/// would hand them script execution on the recipient's origin.
/// </para>
/// </remarks>
public sealed class BlobDescriptor
{
    /// <summary>
    /// Describes a stored blob.
    /// </summary>
    /// <param name="path">The tenant-scoped path.</param>
    /// <param name="length">Stored length in bytes.</param>
    /// <param name="contentHash">
    /// Lowercase hex SHA-256 of the plaintext, computed by the platform.
    /// </param>
    /// <param name="classification">The declared classification.</param>
    /// <param name="declaredContentType">The media type the uploader claimed.</param>
    /// <param name="servedContentType">The media type the platform will serve.</param>
    /// <param name="requiresAttachmentDisposition">
    /// Whether the blob must be downloaded rather than rendered inline.
    /// </param>
    /// <param name="isEncryptedAtRest">Whether the stored bytes are ciphertext.</param>
    /// <param name="createdUtc">When the blob was first written.</param>
    /// <param name="scanState">What is known about malware scanning.</param>
    /// <param name="isCompressed">Whether the stored bytes are compressed.</param>
    /// <param name="version">
    /// Which version this descriptor describes. 1 is the first write; a new
    /// write to the same path increments it.
    /// </param>
    /// <param name="retainUntilUtc">
    /// The earliest instant a lifecycle sweep may delete this blob. Null means
    /// no retention was declared and lifecycle will not touch it.
    /// </param>
    /// <exception cref="ArgumentNullException">Any reference argument is null.</exception>
    public BlobDescriptor(
        BlobPath path,
        long length,
        string contentHash,
        DataClassificationLevel classification,
        string declaredContentType,
        string servedContentType,
        bool requiresAttachmentDisposition,
        bool isEncryptedAtRest,
        DateTimeOffset createdUtc,
        ScanState scanState = ScanState.NotScanned,
        bool isCompressed = false,
        int version = 1,
        DateTimeOffset? retainUntilUtc = null)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        ContentHash = contentHash ?? throw new ArgumentNullException(nameof(contentHash));
        DeclaredContentType = declaredContentType ?? throw new ArgumentNullException(nameof(declaredContentType));
        ServedContentType = servedContentType ?? throw new ArgumentNullException(nameof(servedContentType));
        Length = length;
        Classification = classification;
        RequiresAttachmentDisposition = requiresAttachmentDisposition;
        IsEncryptedAtRest = isEncryptedAtRest;
        CreatedUtc = createdUtc;
        ScanState = scanState;
        IsCompressed = isCompressed;
        Version = version;
        RetainUntilUtc = retainUntilUtc;
    }

    /// <summary>What is known about malware scanning of this blob.</summary>
    public ScanState ScanState { get; }

    /// <summary>Whether the stored bytes are compressed.</summary>
    /// <remarks>
    /// Compression happens **before** encryption. The reverse order compresses
    /// ciphertext, which is incompressible by construction and therefore pure
    /// cost. The known trade is that compressed-then-encrypted length leaks
    /// something about the plaintext (the CRIME/BREACH family); at rest, with
    /// no attacker-chosen plaintext being injected per request, that is an
    /// accepted and stated risk rather than an overlooked one.
    /// </remarks>
    public bool IsCompressed { get; }

    /// <summary>Which version this descriptor describes. The first write is 1.</summary>
    public int Version { get; }

    /// <summary>
    /// The earliest instant a lifecycle sweep may delete this blob, or null when
    /// no retention was declared.
    /// </summary>
    public DateTimeOffset? RetainUntilUtc { get; }

    /// <summary>The tenant-scoped path.</summary>
    public BlobPath Path { get; }

    /// <summary>Stored length in bytes.</summary>
    public long Length { get; }

    /// <summary>
    /// Lowercase hex SHA-256 of the plaintext, computed by the platform on the
    /// bytes it actually received.
    /// </summary>
    /// <remarks>
    /// Deliberately not accepted from the caller. A hash the uploader supplies
    /// certifies nothing: it proves only that the uploader can compute a hash of
    /// whatever they choose to claim they sent.
    /// </remarks>
    public string ContentHash { get; }

    /// <summary>The declared classification.</summary>
    public DataClassificationLevel Classification { get; }

    /// <summary>The media type the uploader claimed. Recorded for audit.</summary>
    public string DeclaredContentType { get; }

    /// <summary>
    /// The media type the platform is willing to serve, which may not be the
    /// declared one.
    /// </summary>
    public string ServedContentType { get; }

    /// <summary>
    /// True when the blob must be served as a download rather than rendered
    /// inline.
    /// </summary>
    /// <remarks>
    /// Stored rather than derived from a comparison of the two content types.
    /// A derived flag would read <c>text/plain; charset=utf-8</c> against a
    /// canonicalised <c>text/plain</c> as a disagreement and force an
    /// unnecessary download — and, worse, the same derivation would have to be
    /// exactly right in the other direction to stay a security control.
    /// </remarks>
    public bool RequiresAttachmentDisposition { get; }

    /// <summary>True when the stored bytes are ciphertext.</summary>
    public bool IsEncryptedAtRest { get; }

    /// <summary>When the blob was first written.</summary>
    public DateTimeOffset CreatedUtc { get; }
}
