using System;
using Edpf.Abstractions.Metadata;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;
using Edpf.Core.Guards;

namespace Edpf.Metadata;

/// <summary>
/// One field's definition (Phase 05b).
/// </summary>
/// <remarks>
/// <para>
/// The same type describes a compiled property and a field a tenant created
/// this morning. <see cref="IsRuntimeDefined"/> records which it was, and
/// **nothing in the framework branches on it to decide protection** —
/// protection follows <see cref="Classification"/> alone.
/// </para>
/// <para>
/// That is the entire point. Two code paths would mean two sets of bugs, and
/// the runtime path — the one carrying fields nobody reviewed at compile time
/// — would be the weaker of the two.
/// </para>
/// </remarks>
public sealed class FieldMetadata : IFieldMetadata
{
    /// <summary>Initializes a field definition.</summary>
    /// <param name="name">The logical name used in queries.</param>
    /// <param name="columnName">The physical column name.</param>
    /// <param name="clrType">The value type.</param>
    /// <param name="classification">The data classification, which drives every protection.</param>
    /// <param name="isFilterable">Whether the field may appear in a filter.</param>
    /// <param name="isSortable">Whether the field may appear in a sort.</param>
    /// <param name="isProjectable">Whether the field may appear in a projection.</param>
    /// <param name="isRuntimeDefined">Whether the field was defined at runtime.</param>
    /// <param name="storageStrategy">Where the value physically lives.</param>
    /// <param name="requiredScope">The scope needed to read the field, if any.</param>
    /// <param name="displayName">The human-readable label; defaults to <paramref name="name"/>.</param>
    public FieldMetadata(
        string name,
        string columnName,
        Type clrType,
        DataClassificationLevel classification,
        bool isFilterable = false,
        bool isSortable = false,
        bool isProjectable = true,
        bool isRuntimeDefined = false,
        FieldStorageStrategy storageStrategy = FieldStorageStrategy.TypedColumn,
        string? requiredScope = null,
        string? displayName = null)
    {
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name));
        ColumnName = Guard.NotNullOrWhiteSpace(columnName, nameof(columnName));
        ClrType = Guard.NotNull(clrType, nameof(clrType));
        Classification = classification;
        IsSortable = isSortable;
        IsProjectable = isProjectable;
        IsRuntimeDefined = isRuntimeDefined;
        StorageStrategy = storageStrategy;
        RequiredScope = requiredScope;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName!;

        // A filterable encrypted field is a contradiction the caller should not
        // be able to declare by accident: filtering ciphertext either fails
        // outright or, under deterministic encryption, leaks frequency
        // information. Refusing here is cheaper than discovering it in a
        // penetration test.
        if (isFilterable && ProtectionPolicy.Default.For(classification).HasFlagSet(
                DataProtectionRequirements.EncryptAtRest))
        {
            throw new ArgumentException(
                $"Field '{name}' is classified {classification}, which requires encryption at rest, so it "
                + "cannot be filterable. Filtering ciphertext either fails or leaks frequency information. "
                + "Add a searchable derived field (a blind index or a coarsened bucket) and mark that "
                + "filterable instead.",
                nameof(isFilterable));
        }

        IsFilterable = isFilterable;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string ColumnName { get; }

    /// <inheritdoc />
    public Type ClrType { get; }

    /// <inheritdoc />
    public bool IsFilterable { get; }

    /// <inheritdoc />
    public bool IsSortable { get; }

    /// <inheritdoc />
    public bool IsProjectable { get; }

    /// <inheritdoc />
    public DataClassificationLevel Classification { get; }

    /// <inheritdoc />
    public bool IsRuntimeDefined { get; }

    /// <inheritdoc />
    public FieldStorageStrategy StorageStrategy { get; }

    /// <inheritdoc />
    public string? RequiredScope { get; }

    /// <inheritdoc />
    public string DisplayName { get; }
}
