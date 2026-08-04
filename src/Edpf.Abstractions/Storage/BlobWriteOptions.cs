using System;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Storage;

/// <summary>
/// What a caller must declare before a blob can be written (Phase 14).
/// </summary>
/// <remarks>
/// <para>
/// **There is no default.** Every field on this type is a constructor
/// parameter, because each one is a decision the platform cannot make on the
/// caller's behalf and must not guess:
/// </para>
/// <list type="bullet">
///   <item>
///     Classification determines whether the bytes are encrypted at rest. A
///     default of <see cref="DataClassificationLevel.Public"/> would silently
///     write PHI in the clear the first time someone forgot the argument.
///   </item>
///   <item>
///     The content type determines how the artefact behaves on the recipient's
///     machine. A default would be a default answer to "is this safe to render",
///     and that answer is never safe to assume.
///   </item>
///   <item>
///     The maximum length bounds the read. A store without one reads until the
///     caller stops sending, which is a denial-of-service the storage layer
///     handed out for free.
///   </item>
/// </list>
/// </remarks>
public sealed class BlobWriteOptions
{
    /// <summary>The largest maximum a caller may declare: 2 GiB.</summary>
    /// <remarks>
    /// An absolute ceiling exists so that "declare your own limit" cannot be
    /// answered with <see cref="long.MaxValue"/>. Larger payloads are a
    /// different problem — chunked or multipart transfer — and should be
    /// refused here rather than half-supported.
    /// </remarks>
    public const long AbsoluteMaxLength = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Declares a write.
    /// </summary>
    /// <param name="classification">
    /// The classification of the content. <see cref="DataClassificationLevel.Confidential"/>
    /// and above are encrypted at rest, and the write is refused outright if no
    /// crypto provider is configured.
    /// </param>
    /// <param name="declaredContentType">
    /// The media type the caller claims. It is recorded, never trusted — see
    /// <see cref="BlobDescriptor.ServedContentType"/>.
    /// </param>
    /// <param name="maxLength">
    /// The largest acceptable payload in bytes. Must be positive and no greater
    /// than <see cref="AbsoluteMaxLength"/>.
    /// </param>
    /// <param name="subjectId">
    /// The data subject, when the blob belongs to one. Supplying it binds the
    /// blob to that subject's key, so crypto-shredding the subject destroys
    /// this blob with the rest of their data (ADR-006). Omitting it falls back
    /// to the tenant key, and the blob then survives subject erasure — which is
    /// occasionally right and usually a mistake.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The content type is blank, or the maximum length is out of range.
    /// </exception>
    public BlobWriteOptions(
        DataClassificationLevel classification,
        string declaredContentType,
        long maxLength,
        Guid? subjectId = null)
    {
        if (string.IsNullOrWhiteSpace(declaredContentType))
        {
            throw new ArgumentException(
                "A declared content type is required. It is recorded, not trusted.",
                nameof(declaredContentType));
        }

        if (maxLength <= 0 || maxLength > AbsoluteMaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxLength),
                "The declared maximum length must be positive and no greater than AbsoluteMaxLength.");
        }

        if (subjectId == Guid.Empty)
        {
            throw new ArgumentException(
                "An empty subject id is not a subject; omit the argument instead.", nameof(subjectId));
        }

        Classification = classification;
        DeclaredContentType = declaredContentType;
        MaxLength = maxLength;
        SubjectId = subjectId;
    }

    /// <summary>The classification of the content being written.</summary>
    public DataClassificationLevel Classification { get; }

    /// <summary>The media type the caller claims. Recorded, never trusted.</summary>
    public string DeclaredContentType { get; }

    /// <summary>The largest acceptable payload, in bytes.</summary>
    public long MaxLength { get; }

    /// <summary>The data subject this blob belongs to, when there is one.</summary>
    public Guid? SubjectId { get; }

    // There is deliberately no RequiresEncryptionAtRest property here.
    //
    // Writing one is the obvious move — `Classification >= Confidential` — and
    // it is wrong. The protection a level requires is decided by
    // IDataProtectionPolicy, in one table, and that table does *not* say
    // "encrypt" for Pci: payment data is protected by never holding it raw, not
    // by encrypting it. A second threshold restated here would have silently
    // disagreed with the first at exactly one level, which is how a field ends
    // up protected on one path and not the other.
}
