using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;

namespace Edpf.UnitTests.Data;

/// <summary>A field description for query-compiler tests.</summary>
public sealed class TestField : IFieldMetadata
{
    public required string Name { get; init; }

    public required string ColumnName { get; init; }

    public Type ClrType { get; init; } = typeof(string);

    public bool IsFilterable { get; init; } = true;

    public bool IsSortable { get; init; } = true;

    public bool IsProjectable { get; init; } = true;

    public DataClassificationLevel Classification { get; init; } = DataClassificationLevel.Internal;
}

/// <summary>
/// Metadata for a <c>Patient</c>-shaped entity, including the traps: a PHI
/// field that is encrypted and therefore not filterable, and a field that is
/// filterable but not projectable.
/// </summary>
public sealed class TestPatientMetadata : IEntityMetadata
{
    public string EntityName => "Patient";

    public string TableName => "PATIENT";

    public IReadOnlyDictionary<string, IFieldMetadata> Fields { get; } =
        new Dictionary<string, IFieldMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = new TestField { Name = "Id", ColumnName = "Id", ClrType = typeof(Guid) },
            ["TenantId"] = new TestField { Name = "TenantId", ColumnName = "TenantId", ClrType = typeof(Guid) },
            ["GivenName"] = new TestField { Name = "GivenName", ColumnName = "GivenName" },
            ["FamilyName"] = new TestField { Name = "FamilyName", ColumnName = "FamilyName" },
            ["DateOfBirth"] = new TestField
            {
                Name = "DateOfBirth",
                ColumnName = "DateOfBirth",
                ClrType = typeof(DateOnly),
                Classification = DataClassificationLevel.Phi,
            },

            // Encrypted at rest: filtering ciphertext either fails or leaks
            // frequency information, so the field is not filterable.
            ["MedicalRecordNumber"] = new TestField
            {
                Name = "MedicalRecordNumber",
                ColumnName = "MrnEnvelope",
                ClrType = typeof(byte[]),
                IsFilterable = false,
                IsSortable = false,
                Classification = DataClassificationLevel.Phi,
            },

            // Internal bookkeeping: usable in a filter, never returned.
            ["InternalRiskScore"] = new TestField
            {
                Name = "InternalRiskScore",
                ColumnName = "InternalRiskScore",
                ClrType = typeof(int),
                IsProjectable = false,
            },
            ["IsDeleted"] = new TestField { Name = "IsDeleted", ColumnName = "IsDeleted", ClrType = typeof(bool) },
        };

    public Result<IFieldMetadata> ResolveField(string fieldName)
        => Fields.TryGetValue(fieldName, out IFieldMetadata? field)
            ? Result.Success(field)
            : Result.Failure<IFieldMetadata>(new Error(
                ErrorCodes.InvalidFilter,
                $"Field '{fieldName}' is not a queryable field of {EntityName}.",
                ErrorCategory.Validation));
}
