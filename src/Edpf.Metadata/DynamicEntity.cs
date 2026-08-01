using System;
using System.Collections.Generic;
using Edpf.Abstractions.Metadata;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;
using Edpf.Core.Guards;

namespace Edpf.Metadata;

/// <summary>
/// An entity instance whose shape comes from metadata rather than a CLR type
/// (Phase 05b).
/// </summary>
/// <remarks>
/// <para>
/// Every read and write goes through <see cref="IEntityMetadata.ResolveField"/>.
/// An undeclared name cannot be set, so a dynamic entity cannot become a way
/// to carry undeclared — and therefore unclassified, unencrypted, unaudited —
/// data into storage.
/// </para>
/// <para>
/// That closes the failure mode a property bag would otherwise introduce:
/// <c>entity["ssn"] = value</c> storing a national identifier that no
/// classification covers, in a column no encryption touches, in a write no
/// audit records.
/// </para>
/// </remarks>
public sealed class DynamicEntity : IDynamicEntity
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes an instance conforming to <paramref name="metadata"/>.</summary>
    /// <param name="metadata">The entity definition this instance must satisfy.</param>
    /// <param name="tenantId">The owning tenant.</param>
    public DynamicEntity(IEntityMetadata metadata, Guid tenantId)
    {
        Metadata = Guard.NotNull(metadata, nameof(metadata));
        TenantId = Guard.NotDefault(tenantId, nameof(tenantId));
    }

    /// <inheritdoc />
    public string EntityName => Metadata.EntityName;

    /// <inheritdoc />
    public Guid TenantId { get; }

    /// <inheritdoc />
    public IEntityMetadata Metadata { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<string> PopulatedFields => _values.Keys;

    /// <inheritdoc />
    public Result<object?> GetValue(string fieldName)
    {
        Result<IFieldMetadata> resolved = Metadata.ResolveField(fieldName);
        if (resolved.IsFailure)
        {
            return Result.Failure<object?>(resolved.Error!);
        }

        return Result.Success(_values.TryGetValue(resolved.Value.Name, out object? value) ? value : null);
    }

    /// <inheritdoc />
    public Result SetValue(string fieldName, object? value)
    {
        Result<IFieldMetadata> resolved = Metadata.ResolveField(fieldName);
        if (resolved.IsFailure)
        {
            return Result.Failure(resolved.Error!);
        }

        IFieldMetadata field = resolved.Value;

        if (value is not null && !IsAssignable(field.ClrType, value))
        {
            // The declared type is what the storage strategy, the query
            // compiler's parameter binding and the export formatter all rely
            // on. A mistyped value would surface much later, in one of those.
            return Result.Failure(new Error(
                ErrorCodes.ValidationFailed,
                $"Field '{field.Name}' is declared as {field.ClrType.Name} and cannot hold a "
                + $"{value.GetType().Name}.",
                ErrorCategory.Validation));
        }

        _values[field.Name] = value;
        return Result.Success();
    }

    private static bool IsAssignable(Type declared, object value)
    {
        Type target = Nullable.GetUnderlyingType(declared) ?? declared;
        return target.IsInstanceOfType(value);
    }
}
