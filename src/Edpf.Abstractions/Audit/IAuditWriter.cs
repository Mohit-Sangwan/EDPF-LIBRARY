using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Audit;

/// <summary>
/// Appends tamper-evident audit records (§10.1; C4 §12.3). Records chain per
/// tenant: each carries the previous record's hash, so removal or reordering
/// breaks verification. A failed audit write fails the audited operation
/// (BRL-005, <see cref="ErrorCodes.AuditUnavailable"/>) — serving without
/// audit is worse than not serving.
/// </summary>
public interface IAuditWriter
{
    /// <summary>
    /// Tokenizes the subject, chains and appends the record.
    /// </summary>
    /// <param name="auditEvent">The event to record.</param>
    /// <param name="cancellationToken">Cancels before the append; the append itself is atomic.</param>
    /// <returns>
    /// Success once durably appended; failure with
    /// <see cref="ErrorCodes.AuditUnavailable"/> otherwise.
    /// </returns>
    Task<Result> WriteAsync(AuditEventDescriptor auditEvent, CancellationToken cancellationToken);
}
