using System.Collections.Generic;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Query;

/// <summary>
/// What a caller is allowed to filter, sort and project (ADR-018/ADR-030).
/// Field names in a query are resolved through this metadata, never taken as
/// caller text — so a field that does not exist cannot reach a query, and a
/// field the caller may not read cannot be named in one.
/// </summary>
public interface IEntityMetadata
{
    /// <summary>The entity name.</summary>
    string EntityName { get; }

    /// <summary>The physical table or collection.</summary>
    string TableName { get; }

    /// <summary>Every field, by logical name.</summary>
    IReadOnlyDictionary<string, IFieldMetadata> Fields { get; }

    /// <summary>
    /// Resolves a caller-supplied field name.
    /// </summary>
    /// <param name="fieldName">The name the caller asked for.</param>
    /// <returns>
    /// The field, or failure with <see cref="ErrorCodes.InvalidFilter"/>.
    /// The failure names only the field the caller supplied — it never
    /// enumerates valid alternatives, which would turn an error message into
    /// a schema-discovery oracle.
    /// </returns>
    Result<IFieldMetadata> ResolveField(string fieldName);
}

/// <summary>One field's capabilities and classification.</summary>
public interface IFieldMetadata
{
    /// <summary>The logical field name used in queries.</summary>
    string Name { get; }

    /// <summary>The physical column name.</summary>
    string ColumnName { get; }

    /// <summary>The .NET type.</summary>
    System.Type ClrType { get; }

    /// <summary>
    /// True when this field may appear in a filter. Encrypted fields are
    /// generally false: filtering ciphertext either fails or, with
    /// deterministic encryption, leaks frequency information.
    /// </summary>
    bool IsFilterable { get; }

    /// <summary>True when this field may appear in an ORDER BY.</summary>
    bool IsSortable { get; }

    /// <summary>True when this field may appear in a projection.</summary>
    bool IsProjectable { get; }

    /// <summary>The field's data classification, which drives redaction and encryption.</summary>
    DataClassificationLevel Classification { get; }

    /// <summary>
    /// True when this field was defined at runtime rather than compiled in.
    /// </summary>
    /// <remarks>
    /// **Nothing in the framework may branch on this to decide protection.**
    /// It exists for diagnostics, migration tooling and storage planning. A
    /// runtime-defined field is protected by its
    /// <see cref="Classification"/>, on exactly the same code path as a
    /// compiled one — that sameness is what makes custom fields safe
    /// (Appendix I.0).
    /// </remarks>
    bool IsRuntimeDefined { get; }

    /// <summary>Where the value physically lives.</summary>
    Metadata.FieldStorageStrategy StorageStrategy { get; }

    /// <summary>
    /// The authorization scope a caller must hold to read this field, or
    /// <see langword="null"/> when entity-level authorization suffices.
    /// </summary>
    string? RequiredScope { get; }

    /// <summary>The human-readable label, for forms and exports.</summary>
    string DisplayName { get; }
}
