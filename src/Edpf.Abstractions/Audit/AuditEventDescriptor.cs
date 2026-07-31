using System;

namespace Edpf.Abstractions.Audit;

/// <summary>
/// What a caller hands to <see cref="IAuditWriter"/> (C4 §12.3). Carries the
/// raw subject id only transiently — the writer tokenizes it before anything
/// is persisted (BRL-006: an audit record never contains a raw identifier).
/// Payload snapshots arrive already encrypted under the subject DEK, so a
/// shredded subject's audit history keeps its chain but loses its content.
/// </summary>
public sealed class AuditEventDescriptor
{
    /// <summary>
    /// Initializes a descriptor.
    /// </summary>
    /// <param name="eventType">Stable event type name, e.g. <c>PatientCreated</c> (§10.5).</param>
    /// <param name="subjectId">The data subject the event concerns. Tokenized by the writer; never persisted raw.</param>
    /// <param name="correlationId">Correlation id of the causing operation.</param>
    /// <param name="beforeCipher">Encrypted before-image, or null when not applicable.</param>
    /// <param name="afterCipher">Encrypted after-image, or null when not applicable.</param>
    /// <exception cref="ArgumentNullException"><paramref name="eventType"/> or <paramref name="correlationId"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="subjectId"/> is empty.</exception>
    public AuditEventDescriptor(
        string eventType,
        Guid subjectId,
        string correlationId,
        byte[]? beforeCipher = null,
        byte[]? afterCipher = null)
    {
        EventType = eventType ?? throw new ArgumentNullException(nameof(eventType));
        CorrelationId = correlationId ?? throw new ArgumentNullException(nameof(correlationId));

        if (subjectId == Guid.Empty)
        {
            throw new ArgumentException("Subject id must not be empty.", nameof(subjectId));
        }

        SubjectId = subjectId;
        BeforeCipher = beforeCipher;
        AfterCipher = afterCipher;
    }

    /// <summary>Stable event type name (§10.5 domain events catalogue).</summary>
    public string EventType { get; }

    /// <summary>The data subject. Tokenized by the writer before persistence.</summary>
    public Guid SubjectId { get; }

    /// <summary>Correlation id linking the audit record to logs and traces.</summary>
    public string CorrelationId { get; }

    /// <summary>Encrypted before-image (serialized envelope), if any. Treat as immutable.</summary>
    public byte[]? BeforeCipher { get; }

    /// <summary>Encrypted after-image (serialized envelope), if any. Treat as immutable.</summary>
    public byte[]? AfterCipher { get; }
}
