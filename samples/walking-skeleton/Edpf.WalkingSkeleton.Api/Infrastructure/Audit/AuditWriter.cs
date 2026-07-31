using System.Text;
using Edpf.Abstractions.Audit;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Security;
using Edpf.Abstractions.Tenancy;
using Edpf.WalkingSkeleton.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Edpf.WalkingSkeleton.Api.Infrastructure.Audit;

/// <summary>
/// Tamper-evident audit append (C4 §12.3): tokenize the subject, read the
/// chain head, hash canonical bytes ‖ previous hash, append with the head as
/// optimistic concurrency guard — two concurrent writers cannot fork the
/// chain (the unique (TenantId, Sequence) index rejects the loser, which
/// retries from the new head, bounded).
/// </summary>
public sealed class AuditWriter(
    SkeletonDbContext db,
    ITokenizer tokenizer,
    IHashingService hashing,
    ITenantContextAccessor tenantAccessor,
    IClock clock) : IAuditWriter
{
    private const int MaxAppendAttempts = 3;

    public async Task<Result> WriteAsync(AuditEventDescriptor auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        ITenantContext? tenant = tenantAccessor.Current;
        if (tenant is null)
        {
            return Result.Failure(new Error(
                ErrorCodes.TenantScopeViolation, "Audit requires a resolved tenant.", ErrorCategory.NotFound));
        }

        Result<string> token = await tokenizer.TokenizeAsync(
            auditEvent.SubjectId.ToString("D"), tenant.TenantId, cancellationToken);
        if (token.IsFailure)
        {
            return Result.Failure(AuditUnavailable());
        }

        for (int attempt = 1; attempt <= MaxAppendAttempts; attempt++)
        {
            AuditRow? head = await db.AuditEvents
                .Where(a => a.TenantId == tenant.TenantId)
                .OrderByDescending(a => a.Sequence)
                .FirstOrDefaultAsync(cancellationToken);

            long sequence = (head?.Sequence ?? 0) + 1;
            byte[] previousHash = head?.EntryHash ?? [];

            var row = new AuditRow
            {
                AuditId = Guid.NewGuid(),
                TenantId = tenant.TenantId,
                Sequence = sequence,
                OccurredUtc = clock.UtcNow,
                EventType = auditEvent.EventType,
                SubjectToken = token.Value,
                CorrelationId = auditEvent.CorrelationId,
                BeforeCipher = auditEvent.BeforeCipher,
                AfterCipher = auditEvent.AfterCipher,
                PreviousHash = previousHash,
            };
            row.EntryHash = ComputeEntryHash(hashing, row);

            db.AuditEvents.Add(row);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            }
            catch (DbUpdateException) when (attempt < MaxAppendAttempts)
            {
                // A concurrent writer took our sequence: retry from the new head.
                db.Entry(row).State = EntityState.Detached;
            }
            catch (DbUpdateException)
            {
                db.Entry(row).State = EntityState.Detached;
                return Result.Failure(AuditUnavailable());
            }
        }

        return Result.Failure(AuditUnavailable());
    }

    /// <summary>
    /// SHA-256 over canonical bytes ‖ previous hash. Canonicalization fixes
    /// field order, encoding and length prefixes so the hash is reproducible
    /// across providers and .NET versions (C4 §12.3 "why this shape").
    /// </summary>
    internal static byte[] ComputeEntryHash(IHashingService hashing, AuditRow row)
    {
        byte[] canonical = ToCanonicalBytes(row);
        var buffer = new byte[canonical.Length + row.PreviousHash.Length];
        canonical.CopyTo(buffer, 0);
        row.PreviousHash.CopyTo(buffer, canonical.Length);
        return hashing.Sha256(buffer);
    }

    internal static byte[] ToCanonicalBytes(AuditRow row)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(row.AuditId.ToByteArray());
        writer.Write(row.TenantId.ToByteArray());
        writer.Write(row.Sequence);
        writer.Write(row.OccurredUtc.UtcTicks);
        WriteLengthPrefixed(writer, Encoding.UTF8.GetBytes(row.EventType));
        WriteLengthPrefixed(writer, Encoding.UTF8.GetBytes(row.SubjectToken));
        WriteLengthPrefixed(writer, Encoding.UTF8.GetBytes(row.CorrelationId));
        WriteLengthPrefixed(writer, row.BeforeCipher ?? []);
        WriteLengthPrefixed(writer, row.AfterCipher ?? []);

        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteLengthPrefixed(BinaryWriter writer, byte[] data)
    {
        writer.Write(data.Length);
        writer.Write(data);
    }

    private static Error AuditUnavailable() => new(
        ErrorCodes.AuditUnavailable,
        "Audit chain append failed; the operation was refused (BRL-005).",
        ErrorCategory.Internal);
}
