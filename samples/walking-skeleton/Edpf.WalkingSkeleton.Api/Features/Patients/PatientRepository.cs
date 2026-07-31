using System.Text;
using System.Text.Json;
using Edpf.Abstractions.Audit;
using Edpf.Abstractions.Data;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Security;
using Edpf.Abstractions.Tenancy;
using Edpf.WalkingSkeleton.Api.Domain;
using Edpf.WalkingSkeleton.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Edpf.WalkingSkeleton.Api.Features.Patients;

/// <summary>
/// The C4 §12.2 code path, at skeleton scale. Reads: tenant scope (structural,
/// via the context's global filter) → materialise → decrypt (destroyed key →
/// tombstone, never an exception) → audit; a failed audit fails the read
/// (BRL-005). Creates: encrypt under the subject DEK, stage row + outbox
/// message + audit record, commit as ONE local transaction (ADR-003).
/// </summary>
public sealed class PatientRepository(
    SkeletonDbContext db,
    ICryptoProvider crypto,
    IAuditWriter auditWriter,
    ITenantContextAccessor tenantAccessor,
    ICorrelationContextAccessor correlationAccessor,
    IClock clock) : IRepository<Patient, Guid>
{
    public async Task<Result<Patient>> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        Result<ITenantContext> tenant = RequireTenant();
        if (tenant.IsFailure)
        {
            return Result.Failure<Patient>(tenant.Error!);
        }

        PatientRow? row = await db.Patients
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (row is null)
        {
            // Absent and cross-tenant are indistinguishable here by design:
            // the global tenant filter already excluded other tenants' rows.
            return Result.Failure<Patient>(NotFound());
        }

        Result<string> mrn = await DecryptMrnAsync(row, cancellationToken);
        if (mrn.IsFailure)
        {
            return Result.Failure<Patient>(mrn.Error!);
        }

        Result audit = await auditWriter.WriteAsync(
            new AuditEventDescriptor("PatientViewed", row.Id, CorrelationId()),
            cancellationToken);
        if (audit.IsFailure)
        {
            return Result.Failure<Patient>(audit.Error!);
        }

        return Result.Success(ToDomain(row, mrn.Value));
    }

    public async Task<Result<PagedResult<Patient>>> ListAsync(
        PageRequest page, CancellationToken cancellationToken)
    {
        Result<ITenantContext> tenant = RequireTenant();
        if (tenant.IsFailure)
        {
            return Result.Failure<PagedResult<Patient>>(tenant.Error!);
        }

        long total = await db.Patients.LongCountAsync(cancellationToken);

        List<PatientRow> rows = await db.Patients
            .AsNoTracking()
            .OrderBy(p => p.FamilyName).ThenBy(p => p.GivenName).ThenBy(p => p.Id) // stable tiebreaker (BRL-017)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .ToListAsync(cancellationToken);

        var patients = new List<Patient>(rows.Count);
        foreach (PatientRow row in rows)
        {
            Result<string> mrn = await DecryptMrnAsync(row, cancellationToken);
            if (mrn.IsFailure)
            {
                return Result.Failure<PagedResult<Patient>>(mrn.Error!);
            }

            patients.Add(ToDomain(row, mrn.Value));
        }

        Result audit = await auditWriter.WriteAsync(
            new AuditEventDescriptor("PatientListViewed", tenant.Value.TenantId, CorrelationId()),
            cancellationToken);
        if (audit.IsFailure)
        {
            return Result.Failure<PagedResult<Patient>>(audit.Error!);
        }

        return Result.Success(new PagedResult<Patient>(patients, page, total));
    }

    public async Task<Result<Patient>> AddAsync(Patient entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);

        Result<ITenantContext> tenant = RequireTenant();
        if (tenant.IsFailure)
        {
            return Result.Failure<Patient>(tenant.Error!);
        }

        // Field-level encryption under the per-subject DEK: ADR-004 and
        // ADR-007 interacting correctly is Phase 02's core proof.
        KeyScope scope = KeyScope.ForSubject(tenant.Value.TenantId, entity.Id);
        Result<EncryptionEnvelope> envelope = await crypto.EncryptAsync(
            Encoding.UTF8.GetBytes(entity.MedicalRecordNumber), scope, cancellationToken);
        if (envelope.IsFailure)
        {
            return Result.Failure<Patient>(envelope.Error!);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        db.Patients.Add(new PatientRow
        {
            Id = entity.Id,
            TenantId = tenant.Value.TenantId,
            GivenName = entity.GivenName,
            FamilyName = entity.FamilyName,
            DateOfBirth = entity.DateOfBirth,
            MrnEnvelope = envelope.Value.Serialize(),
            CreatedUtc = entity.CreatedUtc,
        });

        // Outbox message in the same transaction (ADR-003). Payload carries
        // the subject id token-side only — no PHI (§10.5).
        db.Outbox.Add(new OutboxRow
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Value.TenantId,
            MessageType = "PatientCreated",
            Payload = JsonSerializer.Serialize(new { PatientId = entity.Id, SchemaVersion = 1 }),
            CorrelationId = CorrelationId(),
            OccurredUtc = clock.UtcNow,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return Result.Failure<Patient>(new Error(
                ErrorCodes.Duplicate, "A patient with this id already exists.", ErrorCategory.Conflict));
        }

        // Audit inside the same transaction: if the chain append fails, the
        // create rolls back with it — the operation and its audit are one
        // atomic fact (BRL-005).
        Result audit = await auditWriter.WriteAsync(
            new AuditEventDescriptor(
                "PatientCreated",
                entity.Id,
                CorrelationId(),
                afterCipher: envelope.Value.Serialize()),
            cancellationToken);
        if (audit.IsFailure)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return Result.Failure<Patient>(audit.Error!);
        }

        await transaction.CommitAsync(cancellationToken);
        return Result.Success(entity);
    }

    private async Task<Result<string>> DecryptMrnAsync(PatientRow row, CancellationToken cancellationToken)
    {
        EncryptionEnvelope envelope = EncryptionEnvelope.Deserialize(row.MrnEnvelope);
        Result<byte[]> plaintext = await crypto.DecryptAsync(envelope, cancellationToken);

        if (plaintext.IsFailure)
        {
            // ADR-006: a destroyed key yields a tombstone, never an error that
            // leaks whether data existed (C4 §12.2 step "key destroyed").
            return plaintext.Error!.Code == ErrorCodes.KeyDestroyed
                ? Result.Success(Patient.ErasedTombstone)
                : Result.Failure<string>(plaintext.Error!);
        }

        return Result.Success(Encoding.UTF8.GetString(plaintext.Value));
    }

    private Result<ITenantContext> RequireTenant()
        => tenantAccessor.Current is { } tenant
            ? Result.Success(tenant)
            : Result.Failure<ITenantContext>(NotFound());

    private string CorrelationId() => correlationAccessor.Current?.CorrelationId ?? "none";

    private static Patient ToDomain(PatientRow row, string mrn) => new()
    {
        Id = row.Id,
        TenantId = row.TenantId,
        GivenName = row.GivenName,
        FamilyName = row.FamilyName,
        DateOfBirth = row.DateOfBirth,
        MedicalRecordNumber = mrn,
        CreatedUtc = row.CreatedUtc,
    };

    private static Error NotFound() => new(
        ErrorCodes.NotFound, "The requested resource was not found.", ErrorCategory.NotFound);
}
