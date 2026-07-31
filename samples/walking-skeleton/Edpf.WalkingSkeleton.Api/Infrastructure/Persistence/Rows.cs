namespace Edpf.WalkingSkeleton.Api.Infrastructure.Persistence;

/// <summary>
/// Persistence rows. Kept separate from the domain model so the PHI field
/// exists only as ciphertext at this layer — the raw database never sees a
/// plaintext medical record number (Phase 02 demonstration 5).
/// </summary>
public sealed class PatientRow
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string GivenName { get; set; } = string.Empty;
    public string FamilyName { get; set; } = string.Empty;
    public DateOnly DateOfBirth { get; set; }

    /// <summary>Serialized <c>EncryptionEnvelope</c> holding the MRN ciphertext.</summary>
    public byte[] MrnEnvelope { get; set; } = [];

    public DateTimeOffset CreatedUtc { get; set; }
}

/// <summary>A provisioned tenant (seeded for the skeleton; Phase 12 generalizes).</summary>
public sealed class TenantRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public int IsolationMode { get; set; }
    public Guid KekReference { get; set; }
}

/// <summary>What a stored key is for.</summary>
public enum KeyPurpose
{
    /// <summary>Data-encryption key (tenant- or subject-scoped).</summary>
    DataEncryption = 0,

    /// <summary>Tenant-scoped HMAC salt for subject tokenization (C4 §12.3).</summary>
    AuditSalt = 1,

    /// <summary>Tenant key-encryption key, wrapped by the master key.</summary>
    KeyEncryption = 2,
}

/// <summary>
/// A wrapped key. DEKs are wrapped by the tenant KEK; the KEK by the master
/// key. Raw key material never rests unwrapped. Destroying a row's material
/// is the erasure primitive (ADR-006).
/// </summary>
public sealed class KeyRow
{
    public Guid KeyId { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>The data subject for subject-scoped DEKs; null for tenant-scoped keys.</summary>
    public Guid? SubjectId { get; set; }

    public KeyPurpose Purpose { get; set; }
    public int KeyVersion { get; set; }

    /// <summary>The wrapped key material (serialized envelope). Zeroed on destruction.</summary>
    public byte[] WrappedKey { get; set; } = [];

    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>When the key was crypto-shredded; null while live.</summary>
    public DateTimeOffset? DestroyedUtc { get; set; }
}

/// <summary>
/// One link of the tamper-evident audit chain (C4 §12.3). Subject appears
/// only as a token (BRL-006); payload snapshots only as ciphertext under the
/// subject DEK, so erasure removes content but never breaks the chain.
/// </summary>
public sealed class AuditRow
{
    public Guid AuditId { get; set; }
    public Guid TenantId { get; set; }
    public long Sequence { get; set; }
    public DateTimeOffset OccurredUtc { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string SubjectToken { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public byte[]? BeforeCipher { get; set; }
    public byte[]? AfterCipher { get; set; }
    public byte[] PreviousHash { get; set; } = [];
    public byte[] EntryHash { get; set; } = [];
}

/// <summary>
/// A transactional outbox message (ADR-003): enqueued in the same local
/// transaction as the state change, dispatched afterwards, at least once.
/// Payload carries subject tokens, never raw identifiers (§10.5).
/// </summary>
public sealed class OutboxRow
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string MessageType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public DateTimeOffset OccurredUtc { get; set; }
    public DateTimeOffset? DispatchedUtc { get; set; }
    public int Attempts { get; set; }
}

/// <summary>A completed idempotent request (ADR-003; ADR-012 stage 6).</summary>
public sealed class IdempotencyRow
{
    public Guid TenantId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public int ResponseStatusCode { get; set; }
    public string ResponseBody { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
}
