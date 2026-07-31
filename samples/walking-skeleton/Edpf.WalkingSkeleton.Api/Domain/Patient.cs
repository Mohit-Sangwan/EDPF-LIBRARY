using Edpf.Abstractions.Primitives;

namespace Edpf.WalkingSkeleton.Api.Domain;

/// <summary>
/// The one entity of the walking skeleton (Phase 02 §③), with a deliberately
/// PHI-classified field. The medical record number is encrypted at rest under
/// a per-subject DEK (ADR-006/ADR-007); after erasure it decrypts to a
/// tombstone, never to data.
/// </summary>
public sealed record Patient
{
    /// <summary>Rendered in place of PHI whose subject key was destroyed (ADR-006).</summary>
    public const string ErasedTombstone = "[erased]";

    public required Guid Id { get; init; }

    public required Guid TenantId { get; init; }

    [DataClassification(DataClassificationLevel.Pii)]
    public required string GivenName { get; init; }

    [DataClassification(DataClassificationLevel.Pii)]
    public required string FamilyName { get; init; }

    [DataClassification(DataClassificationLevel.Phi)]
    public required DateOnly DateOfBirth { get; init; }

    /// <summary>PHI. Encrypted at rest; tombstoned after crypto-shredding.</summary>
    [DataClassification(DataClassificationLevel.Phi)]
    public required string MedicalRecordNumber { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }
}
