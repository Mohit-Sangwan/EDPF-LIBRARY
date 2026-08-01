using System;
using System.Collections.Generic;
using System.Reflection;
using Edpf.Abstractions.Metadata;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;
using Edpf.Core.Guards;

namespace Edpf.Metadata;

/// <summary>
/// Builds metadata for a compiled type from its
/// <see cref="DataClassificationAttribute"/> tags (Phase 05b).
/// </summary>
/// <remarks>
/// <para>
/// Reflection has one legitimate job here: **producing** metadata for compiled
/// types at startup, so that a developer declaring a property does not also
/// have to hand-write a definition for it. Reflection never *resolves* a query
/// field — that goes through <see cref="IMetadataRepository"/>, which is the
/// correction Appendix I.0 demanded.
/// </para>
/// <para>
/// The output of this scanner and a tenant's runtime overlay are the same
/// type, land in the same dictionary, and are protected by the same policy.
/// A consumer cannot tell which produced a given field, and none tries.
/// </para>
/// </remarks>
public static class CompiledEntityScanner
{
    /// <summary>
    /// Scans <paramref name="entityType"/> into entity metadata.
    /// </summary>
    /// <param name="entityType">The compiled entity type.</param>
    /// <param name="tableName">The physical table; defaults to the type name.</param>
    /// <returns>The entity metadata.</returns>
    /// <remarks>
    /// A property with no <see cref="DataClassificationAttribute"/> is treated
    /// as <see cref="DataClassificationLevel.Internal"/> rather than
    /// <see cref="DataClassificationLevel.Public"/>. Forgetting to classify is
    /// the common mistake, and it must not be the mistake that publishes data.
    /// </remarks>
    public static EntityMetadata Scan(Type entityType, string? tableName = null)
    {
        Guard.NotNull(entityType, nameof(entityType));

        DataClassificationLevel typeDefault =
            entityType.GetCustomAttribute<DataClassificationAttribute>()?.Level
            ?? DataClassificationLevel.Internal;

        var fields = new List<IFieldMetadata>();

        foreach (PropertyInfo property in entityType.GetProperties(
                     BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            DataClassificationLevel classification =
                property.GetCustomAttribute<DataClassificationAttribute>()?.Level ?? typeDefault;

            DataProtectionRequirements protections = ProtectionPolicy.Default.For(classification);

            // Filterability is derived, not asked for: a field requiring
            // encryption at rest cannot be filtered, and deriving it here means
            // a developer cannot accidentally opt a PHI column into a WHERE
            // clause by adding one attribute and forgetting another.
            bool queryable = !protections.HasFlagSet(DataProtectionRequirements.EncryptAtRest)
                && !protections.HasFlagSet(DataProtectionRequirements.TokenizeNeverStoreRaw);

            fields.Add(new FieldMetadata(
                name: property.Name,
                columnName: property.Name,
                clrType: property.PropertyType,
                classification: classification,
                isFilterable: queryable,
                isSortable: queryable,
                isProjectable: true,
                isRuntimeDefined: false,
                storageStrategy: FieldStorageStrategy.TypedColumn,
                requiredScope: null,
                displayName: property.Name));
        }

        return new EntityMetadata(
            entityType.Name,
            string.IsNullOrWhiteSpace(tableName) ? entityType.Name : tableName!,
            fields);
    }
}
