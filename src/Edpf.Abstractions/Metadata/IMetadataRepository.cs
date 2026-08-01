using System;
using System.Collections.Generic;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;

namespace Edpf.Abstractions.Metadata;

/// <summary>
/// Where a field physically lives (Phase 05b). Chosen by policy, with the
/// trade-off recorded rather than assumed.
/// </summary>
public enum FieldStorageStrategy
{
    /// <summary>A dedicated column. Fastest; requires a migration to add.</summary>
    TypedColumn = 0,

    /// <summary>
    /// A sparse column. Cheap when most rows leave it null, which is the usual
    /// shape of a custom field; engine-specific.
    /// </summary>
    SparseColumn = 1,

    /// <summary>
    /// A key within a JSON document column. No migration to add a field;
    /// indexing and typed querying are weaker.
    /// </summary>
    JsonColumn = 2,

    /// <summary>
    /// Entity-attribute-value rows. Unlimited fields with no schema change,
    /// and the worst query performance of the four — chosen when a tenant's
    /// field count is unbounded, never as a default.
    /// </summary>
    EntityAttributeValue = 3,
}

/// <summary>
/// The source of every entity and field definition the framework knows about
/// (Phase 05b, <see cref="IEntityMetadata"/>'s missing other half).
/// </summary>
/// <remarks>
/// <para>
/// **This exists because of an ordering defect the master document found in
/// its own plan (Appendix I.0).** The dynamic-query safety model resolves
/// caller-supplied filter, sort and projection fields against entity metadata
/// — but no metadata repository existed, so the query layer was implicitly
/// assuming reflection over compile-time types.
/// </para>
/// <para>
/// That assumption breaks the moment a customer adds a custom field, which
/// they do in week one. Reflection cannot describe a runtime-defined field, so
/// a whitelist built on reflection cannot authorize one — and the framework
/// would push custom fields outside the safety model rather than inside it.
/// </para>
/// <para>
/// The repository therefore returns compile-time and runtime-defined fields
/// **through one interface, indistinguishable to every consumer.** That
/// sameness is the security property: there is no second path for custom
/// fields to travel along, so there is no second path to secure.
/// </para>
/// </remarks>
public interface IMetadataRepository
{
    /// <summary>
    /// Resolves an entity's metadata as it stood at a point in time.
    /// </summary>
    /// <param name="entityName">The logical entity name.</param>
    /// <param name="tenantId">
    /// The tenant whose overlay applies. One tenant's custom fields are
    /// invisible to another — metadata is tenant-scoped data, and leaking a
    /// field *name* can disclose a business fact even with no value attached.
    /// </param>
    /// <param name="asOf">
    /// The point in time to resolve. A form rendered in 2024 must reproduce
    /// exactly in an audit five years later, so metadata is effective-dated
    /// rather than overwritten.
    /// </param>
    /// <returns>
    /// The entity metadata, or <see cref="ErrorCodes.NotFound"/>. The failure
    /// never enumerates known entities.
    /// </returns>
    Result<IEntityMetadata> GetEntity(string entityName, Guid tenantId, DateTimeOffset asOf);
}

/// <summary>
/// An entity instance whose shape is known only at runtime (Phase 05b).
/// </summary>
/// <remarks>
/// Values are reachable only by a name the backing metadata declares. A field
/// the tenant has not defined cannot be read or written, so a dynamic entity
/// cannot become a way to smuggle undeclared — and therefore unclassified,
/// unencrypted, unaudited — data into storage.
/// </remarks>
public interface IDynamicEntity
{
    /// <summary>The entity name this instance conforms to.</summary>
    string EntityName { get; }

    /// <summary>The owning tenant.</summary>
    Guid TenantId { get; }

    /// <summary>The metadata this instance was validated against.</summary>
    IEntityMetadata Metadata { get; }

    /// <summary>The field names carrying a value on this instance.</summary>
    IReadOnlyCollection<string> PopulatedFields { get; }

    /// <summary>
    /// Reads a field value.
    /// </summary>
    /// <param name="fieldName">The logical field name.</param>
    /// <returns>The value, or a failure if the field is not declared.</returns>
    Result<object?> GetValue(string fieldName);

    /// <summary>
    /// Writes a field value.
    /// </summary>
    /// <param name="fieldName">The logical field name.</param>
    /// <param name="value">The value, which must match the declared type.</param>
    /// <returns>Success, or a failure if the field is undeclared or mistyped.</returns>
    Result SetValue(string fieldName, object? value);
}
