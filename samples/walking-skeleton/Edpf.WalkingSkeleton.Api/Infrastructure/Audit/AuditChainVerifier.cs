using Edpf.Abstractions.Audit;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Security;
using Edpf.WalkingSkeleton.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Edpf.WalkingSkeleton.Api.Infrastructure.Audit;

/// <summary>
/// Recomputes every link of a tenant's chain (C4 §12.3). Crypto-shredding a
/// subject leaves this verification intact — that invariant is Phase 02
/// demonstration 6, and Spike-C before it.
/// </summary>
public sealed class AuditChainVerifier(SkeletonDbContext db, IHashingService hashing) : IAuditChainVerifier
{
    public async Task<Result<AuditChainVerification>> VerifyAsync(
        Guid tenantId, CancellationToken cancellationToken)
    {
        List<AuditRow> rows = await db.AuditEvents
            .Where(a => a.TenantId == tenantId)
            .OrderBy(a => a.Sequence)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        byte[] expectedPrevious = [];
        long expectedSequence = 1;

        foreach (AuditRow row in rows)
        {
            bool linkValid =
                row.Sequence == expectedSequence
                && row.PreviousHash.AsSpan().SequenceEqual(expectedPrevious)
                && row.EntryHash.AsSpan().SequenceEqual(AuditWriter.ComputeEntryHash(hashing, row));

            if (!linkValid)
            {
                return Result.Success(new AuditChainVerification(false, rows.Count, row.Sequence));
            }

            expectedPrevious = row.EntryHash;
            expectedSequence++;
        }

        return Result.Success(new AuditChainVerification(true, rows.Count, null));
    }
}
