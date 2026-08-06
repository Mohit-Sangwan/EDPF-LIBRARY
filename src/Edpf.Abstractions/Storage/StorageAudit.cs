using System;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Storage;

/// <summary>What was done to a blob.</summary>
public enum StorageOperation
{
    /// <summary>A blob was written or replaced.</summary>
    Write = 0,

    /// <summary>A blob's content was read.</summary>
    Read = 1,

    /// <summary>A blob was deleted.</summary>
    Delete = 2,

    /// <summary>A blob's metadata was read, without its content.</summary>
    Stat = 3,

    /// <summary>Text was extracted from a blob.</summary>
    Extract = 4,
}

/// <summary>
/// One recorded access to stored content. Metadata only.
/// </summary>
/// <remarks>
/// <para>
/// **There is no field here that can hold content**, and that is deliberate
/// rather than incidental. An audit trail over a clinical document store is
/// itself a target: if it recorded what was read as well as that it was read,
/// compromising the audit log would disclose the records it was protecting.
/// </para>
/// <para>
/// The content hash is included because it identifies *which* bytes without
/// being them — enough to prove two accesses saw the same version, and useless
/// to an attacker who has only the log.
/// </para>
/// </remarks>
public sealed class StorageAuditEvent
{
    /// <summary>
    /// Records an access.
    /// </summary>
    /// <param name="operation">What was done.</param>
    /// <param name="path">Which blob.</param>
    /// <param name="classification">What the blob is.</param>
    /// <param name="contentHash">Which bytes, or null when the operation had none.</param>
    /// <param name="succeeded">Whether it succeeded.</param>
    /// <param name="errorCode">The stable error code when it failed, never a message.</param>
    /// <param name="occurredUtc">When.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public StorageAuditEvent(
        StorageOperation operation,
        BlobPath path,
        DataClassificationLevel classification,
        string? contentHash,
        bool succeeded,
        string? errorCode,
        DateTimeOffset occurredUtc)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Operation = operation;
        Classification = classification;
        ContentHash = contentHash;
        Succeeded = succeeded;
        ErrorCode = errorCode;
        OccurredUtc = occurredUtc;
    }

    /// <summary>What was done.</summary>
    public StorageOperation Operation { get; }

    /// <summary>Which blob. The path is tenant-prefixed, so the tenant is implicit.</summary>
    public BlobPath Path { get; }

    /// <summary>What the blob is.</summary>
    public DataClassificationLevel Classification { get; }

    /// <summary>Which bytes, or null.</summary>
    public string? ContentHash { get; }

    /// <summary>
    /// Whether it succeeded.
    /// </summary>
    /// <remarks>
    /// Failed attempts are recorded too. A log of successes cannot answer
    /// "did anyone try to reach this record", which is the question asked
    /// after a breach.
    /// </remarks>
    public bool Succeeded { get; }

    /// <summary>The stable error code when it failed. A code, never a message.</summary>
    public string? ErrorCode { get; }

    /// <summary>When it happened.</summary>
    public DateTimeOffset OccurredUtc { get; }
}

/// <summary>
/// Where storage access records go.
/// </summary>
/// <remarks>
/// <para>
/// **A failed audit fails the operation** (BRL-005). If the sink cannot record
/// a read, the read does not happen — because HIPAA §164.312(b) requires the
/// access record, and an access that was not recorded is an access that did
/// not lawfully occur. The alternative, serving the content and logging a
/// warning, produces a system whose audit trail is complete only when nothing
/// went wrong.
/// </para>
/// <para>
/// This is the same rule the audit subsystem already applies elsewhere; it is
/// restated here because the storage layer is where people most often argue
/// for an exception on performance grounds.
/// </para>
/// </remarks>
public interface IStorageAuditSink
{
    /// <summary>
    /// Records one access.
    /// </summary>
    /// <param name="auditEvent">What happened.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>
    /// Success once durably recorded. A failure carries
    /// <see cref="ErrorCodes.AuditUnavailable"/> and aborts the operation that
    /// triggered it.
    /// </returns>
    Task<Result> RecordAsync(StorageAuditEvent auditEvent, CancellationToken cancellationToken);
}
