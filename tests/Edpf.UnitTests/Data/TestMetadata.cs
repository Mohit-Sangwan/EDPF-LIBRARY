using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;
using Edpf.Metadata;

namespace Edpf.UnitTests.Data;

/// <summary>
/// Metadata for a subject-shaped entity, including the traps: a PHI field that
/// is encrypted and therefore not filterable, and a field that is filterable
/// but not projectable.
/// </summary>
/// <remarks>
/// Built on the production <see cref="EntityMetadata"/> and
/// <see cref="FieldMetadata"/> rather than hand-rolled doubles. Since
/// Phase 05b there is a real implementation, and testing the query compiler
/// against a double would leave the pairing of compiler and repository — the
/// pairing that actually ships — unexercised.
/// </remarks>
public static class TestEntities
{
    /// <summary>Builds the test entity's metadata.</summary>
    /// <returns>The metadata.</returns>
    public static EntityMetadata SubjectRecord() => new(
        "SubjectRecord",
        "SUBJECT_RECORD",
        [
            new FieldMetadata("Id", "Id", typeof(Guid), DataClassificationLevel.Internal,
                isFilterable: true, isSortable: true),
            new FieldMetadata("TenantId", "TenantId", typeof(Guid), DataClassificationLevel.Internal,
                isFilterable: true, isSortable: true),
            new FieldMetadata("GivenName", "GivenName", typeof(string), DataClassificationLevel.Internal,
                isFilterable: true, isSortable: true),
            new FieldMetadata("FamilyName", "FamilyName", typeof(string), DataClassificationLevel.Internal,
                isFilterable: true, isSortable: true),

            // Classified, therefore encrypted at rest, therefore not
            // filterable — FieldMetadata refuses the contradiction, so this
            // reads as a declaration rather than three flags to keep in sync.
            new FieldMetadata("DateOfBirth", "DateOfBirth", typeof(DateTime), DataClassificationLevel.Phi),
            new FieldMetadata("RecordNumber", "RecordNumberEnvelope", typeof(byte[]),
                DataClassificationLevel.Phi),

            // Internal bookkeeping: usable in a filter, never returned.
            new FieldMetadata("InternalRiskScore", "InternalRiskScore", typeof(int),
                DataClassificationLevel.Internal, isFilterable: true, isSortable: true,
                isProjectable: false),
            new FieldMetadata("IsDeleted", "IsDeleted", typeof(bool), DataClassificationLevel.Internal,
                isFilterable: true, isSortable: true),
        ]);
}
