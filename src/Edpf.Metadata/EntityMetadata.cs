using System;
using System.Collections.Generic;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;
using Edpf.Core.Guards;

namespace Edpf.Metadata;

/// <summary>
/// An entity's fields, compile-time and runtime-defined alike (Phase 05b).
/// </summary>
/// <remarks>
/// Consumers cannot tell the two apart without asking, and nothing in the
/// framework asks. <see cref="ResolveField"/> is the single door every
/// caller-supplied field name goes through, which is what lets the query layer
/// authorize a field a customer invented after the binary shipped.
/// </remarks>
public sealed class EntityMetadata : IEntityMetadata
{
    private readonly Dictionary<string, IFieldMetadata> _fields;

    /// <summary>Initializes entity metadata.</summary>
    /// <param name="entityName">The logical entity name.</param>
    /// <param name="tableName">The physical table or collection.</param>
    /// <param name="fields">The fields.</param>
    /// <exception cref="ArgumentException">Two fields share a name.</exception>
    public EntityMetadata(string entityName, string tableName, IEnumerable<IFieldMetadata> fields)
    {
        EntityName = Guard.NotNullOrWhiteSpace(entityName, nameof(entityName));
        TableName = Guard.NotNullOrWhiteSpace(tableName, nameof(tableName));
        Guard.NotNull(fields, nameof(fields));

        // Ordinal-ignore-case, deliberately: a caller writing "patientid" must
        // resolve to the same field as "PatientId", but the comparison must not
        // be culture-sensitive — under a Turkish culture "I".ToLower() is "ı",
        // and a field name would resolve differently depending on the server's
        // locale (Phase 27).
        _fields = new Dictionary<string, IFieldMetadata>(StringComparer.OrdinalIgnoreCase);

        foreach (IFieldMetadata field in fields)
        {
            if (_fields.ContainsKey(field.Name))
            {
                throw new ArgumentException(
                    $"Entity '{entityName}' declares more than one field named '{field.Name}'. Which one a "
                    + "query resolved to would depend on ordering, and one of them may be classified.",
                    nameof(fields));
            }

            _fields[field.Name] = field;
        }
    }

    /// <inheritdoc />
    public string EntityName { get; }

    /// <inheritdoc />
    public string TableName { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, IFieldMetadata> Fields => _fields;

    /// <inheritdoc />
    public Result<IFieldMetadata> ResolveField(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return Result.Failure<IFieldMetadata>(new Error(
                ErrorCodes.InvalidFilter, "A field name is required.", ErrorCategory.Validation));
        }

        if (_fields.TryGetValue(fieldName, out IFieldMetadata? field))
        {
            return Result.Success(field);
        }

        // The message names only what the caller supplied. Listing valid
        // fields would turn a validation error into a schema-discovery oracle,
        // and on a tenant-overlaid entity the field list is itself tenant data.
        return Result.Failure<IFieldMetadata>(new Error(
            ErrorCodes.InvalidFilter,
            $"'{fieldName}' is not a queryable field of '{EntityName}'.",
            ErrorCategory.Validation));
    }
}
