using Edpf.Abstractions.Consistency;
using Edpf.Abstractions.Primitives;
using Edpf.WalkingSkeleton.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Edpf.WalkingSkeleton.Api.Infrastructure.Consistency;

/// <summary>EF-backed idempotency bookkeeping (ADR-003; ADR-012 stage 6).</summary>
public sealed class EfIdempotencyStore(SkeletonDbContext db) : IIdempotencyStore
{
    public async Task<IdempotencyRecord?> FindAsync(
        Guid tenantId, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);

        IdempotencyRow? row = await db.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(
                i => i.TenantId == tenantId && i.IdempotencyKey == idempotencyKey,
                cancellationToken);

        return row is null
            ? null
            : new IdempotencyRecord(
                row.TenantId, row.IdempotencyKey, row.RequestHash,
                row.ResponseStatusCode, row.ResponseBody, row.CreatedUtc);
    }

    public async Task<Result> SaveAsync(IdempotencyRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        db.IdempotencyRecords.Add(new IdempotencyRow
        {
            TenantId = record.TenantId,
            IdempotencyKey = record.IdempotencyKey,
            RequestHash = record.RequestHash,
            ResponseStatusCode = record.ResponseStatusCode,
            ResponseBody = record.ResponseBody,
            CreatedUtc = record.CreatedUtc,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
        catch (DbUpdateException)
        {
            // Concurrent duplicate on the (TenantId, Key) primary key.
            return Result.Failure(new Error(
                ErrorCodes.IdempotencyConflict,
                "The idempotency key was concurrently completed by another request.",
                ErrorCategory.Conflict));
        }
    }
}
