using System;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Audit;

/// <summary>
/// Recomputes and verifies a tenant's audit hash chain (§10.1; C4 §12.3).
/// Run on demand for evidence collection and continuously by operations
/// (Phase 19 wires the scheduled verification).
/// </summary>
public interface IAuditChainVerifier
{
    /// <summary>
    /// Verifies the full chain for a tenant.
    /// </summary>
    /// <param name="tenantId">The tenant whose chain to verify.</param>
    /// <param name="cancellationToken">Cancels the verification.</param>
    /// <returns>The verification outcome, including the first broken link if any.</returns>
    Task<Result<AuditChainVerification>> VerifyAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>The outcome of an audit-chain verification.</summary>
public sealed class AuditChainVerification
{
    /// <summary>
    /// Initializes a verification outcome.
    /// </summary>
    /// <param name="isValid">True when every link verified.</param>
    /// <param name="recordCount">Number of records examined.</param>
    /// <param name="firstBrokenSequence">Sequence of the first broken link, when invalid.</param>
    public AuditChainVerification(bool isValid, long recordCount, long? firstBrokenSequence)
    {
        IsValid = isValid;
        RecordCount = recordCount;
        FirstBrokenSequence = firstBrokenSequence;
    }

    /// <summary>True when every link verified.</summary>
    public bool IsValid { get; }

    /// <summary>Number of records examined.</summary>
    public long RecordCount { get; }

    /// <summary>Sequence number of the first broken link, when invalid.</summary>
    public long? FirstBrokenSequence { get; }
}
