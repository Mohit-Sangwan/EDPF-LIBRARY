using System;
using System.Collections.Generic;
using Edpf.Abstractions.Metadata;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;
using Edpf.Core.Guards;

namespace Edpf.Metadata;

/// <summary>
/// One tenant's addition to a compiled entity, valid over a date range
/// (Phase 05b).
/// </summary>
/// <remarks>
/// Effective dating exists because a form rendered in 2024 must reproduce
/// exactly in an audit five years later. Overwriting a definition would make
/// the historical record unreconstructable, and "the field meant something
/// different then" is not an answer an auditor accepts.
/// </remarks>
public sealed class MetadataOverlay
{
    /// <summary>Initializes an overlay.</summary>
    /// <param name="entityName">The entity being extended.</param>
    /// <param name="tenantId">The tenant the extension belongs to.</param>
    /// <param name="fields">The fields added.</param>
    /// <param name="effectiveFrom">When the definition takes effect, inclusive.</param>
    /// <param name="effectiveTo">When it ceases to apply, exclusive; open-ended if null.</param>
    /// <exception cref="ArgumentException">The effective range is inverted.</exception>
    public MetadataOverlay(
        string entityName,
        Guid tenantId,
        IReadOnlyList<IFieldMetadata> fields,
        DateTimeOffset effectiveFrom,
        DateTimeOffset? effectiveTo = null)
    {
        EntityName = Guard.NotNullOrWhiteSpace(entityName, nameof(entityName));
        TenantId = Guard.NotDefault(tenantId, nameof(tenantId));
        Fields = Guard.NotNull(fields, nameof(fields));
        EffectiveFrom = effectiveFrom;
        EffectiveTo = effectiveTo;

        if (effectiveTo.HasValue && effectiveTo.Value <= effectiveFrom)
        {
            throw new ArgumentException(
                "An overlay's effective-to must follow its effective-from.", nameof(effectiveTo));
        }
    }

    /// <summary>The entity being extended.</summary>
    public string EntityName { get; }

    /// <summary>The tenant the extension belongs to.</summary>
    public Guid TenantId { get; }

    /// <summary>The fields added.</summary>
    public IReadOnlyList<IFieldMetadata> Fields { get; }

    /// <summary>When the definition takes effect, inclusive.</summary>
    public DateTimeOffset EffectiveFrom { get; }

    /// <summary>When it ceases to apply, exclusive.</summary>
    public DateTimeOffset? EffectiveTo { get; }

    /// <summary>
    /// True when this overlay applies at <paramref name="asOf"/>.
    /// </summary>
    /// <param name="asOf">The instant to test.</param>
    /// <returns>Whether the overlay is in effect.</returns>
    public bool AppliesAt(DateTimeOffset asOf)
        => asOf >= EffectiveFrom && (!EffectiveTo.HasValue || asOf < EffectiveTo.Value);
}

/// <summary>
/// The metadata platform (Phase 05b, closing the Appendix I.0 ordering defect).
/// </summary>
/// <remarks>
/// <para>
/// Holds compiled entity definitions and per-tenant overlays, and composes
/// them into a single <see cref="IEntityMetadata"/> whose consumers cannot
/// distinguish the two sources.
/// </para>
/// <para>
/// **Metadata is tenant data.** A field name alone can disclose a business
/// fact — an entity carrying <c>ClinicalTrialArm</c> tells a competitor what
/// that hospital is running, with no value attached. So an overlay is visible
/// only to the tenant that owns it, and a caller asking for another tenant's
/// entity gets the unextended base, not an error that would confirm the
/// extension exists.
/// </para>
/// </remarks>
public sealed class MetadataRepository : IMetadataRepository
{
    private readonly Dictionary<string, EntityMetadata> _compiled =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<MetadataOverlay> _overlays = [];

    /// <summary>
    /// Registers a compiled entity definition.
    /// </summary>
    /// <param name="entity">The entity metadata.</param>
    /// <exception cref="ArgumentException">The entity is already registered.</exception>
    public void RegisterCompiled(EntityMetadata entity)
    {
        Guard.NotNull(entity, nameof(entity));

        if (_compiled.ContainsKey(entity.EntityName))
        {
            throw new ArgumentException(
                $"Entity '{entity.EntityName}' is already registered. A silent replacement could swap a "
                + "classified field definition for an unclassified one.",
                nameof(entity));
        }

        _compiled[entity.EntityName] = entity;
    }

    /// <summary>
    /// Adds a tenant's runtime-defined fields.
    /// </summary>
    /// <param name="overlay">The overlay.</param>
    /// <returns>
    /// Success, or a failure if the base entity is unknown or a field name
    /// collides with a compiled one.
    /// </returns>
    public Result AddOverlay(MetadataOverlay overlay)
    {
        Guard.NotNull(overlay, nameof(overlay));

        if (!_compiled.TryGetValue(overlay.EntityName, out EntityMetadata? baseEntity))
        {
            return Result.Failure(new Error(
                ErrorCodes.NotFound,
                $"No entity named '{overlay.EntityName}' is registered.",
                ErrorCategory.NotFound));
        }

        foreach (IFieldMetadata field in overlay.Fields)
        {
            // A custom field shadowing a compiled one is how a tenant would
            // redefine a Phi field as Public and strip its protections. The
            // overlay is additive only.
            if (baseEntity.Fields.ContainsKey(field.Name))
            {
                return Result.Failure(new Error(
                    ErrorCodes.Duplicate,
                    $"'{field.Name}' is already a field of '{overlay.EntityName}'. A custom field cannot "
                    + "shadow a built-in one, because shadowing would let a tenant redefine a classified "
                    + "field as unclassified and strip its protections.",
                    ErrorCategory.Conflict));
            }

            if (!field.IsRuntimeDefined)
            {
                return Result.Failure(new Error(
                    ErrorCodes.ValidationFailed,
                    $"Overlay field '{field.Name}' must be marked as runtime-defined.",
                    ErrorCategory.Validation));
            }
        }

        // Two overlays adding the same field name over overlapping dates would
        // make resolution order-dependent — and one of the two may be the
        // classified definition.
        foreach (MetadataOverlay existing in _overlays)
        {
            if (existing.TenantId != overlay.TenantId
                || !string.Equals(existing.EntityName, overlay.EntityName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Overlaps(existing, overlay))
            {
                continue;
            }

            foreach (IFieldMetadata field in overlay.Fields)
            {
                foreach (IFieldMetadata existingField in existing.Fields)
                {
                    if (string.Equals(existingField.Name, field.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        return Result.Failure(new Error(
                            ErrorCodes.Duplicate,
                            $"'{field.Name}' is already defined for this tenant over an overlapping "
                            + "effective period. Close the earlier definition before opening a new one.",
                            ErrorCategory.Conflict));
                    }
                }
            }
        }

        _overlays.Add(overlay);
        return Result.Success();
    }

    /// <inheritdoc />
    public Result<IEntityMetadata> GetEntity(string entityName, Guid tenantId, DateTimeOffset asOf)
    {
        if (string.IsNullOrWhiteSpace(entityName))
        {
            return Result.Failure<IEntityMetadata>(new Error(
                ErrorCodes.NotFound, "The requested entity was not found.", ErrorCategory.NotFound));
        }

        if (!_compiled.TryGetValue(entityName, out EntityMetadata? baseEntity))
        {
            return Result.Failure<IEntityMetadata>(new Error(
                ErrorCodes.NotFound,
                $"No entity named '{entityName}' is registered.",
                ErrorCategory.NotFound));
        }

        var composed = new List<IFieldMetadata>(baseEntity.Fields.Count);
        foreach (KeyValuePair<string, IFieldMetadata> pair in baseEntity.Fields)
        {
            composed.Add(pair.Value);
        }

        bool extended = false;

        foreach (MetadataOverlay overlay in _overlays)
        {
            // Tenant scoping and effective dating are applied in the same
            // filter, so there is no ordering in which one could be skipped.
            if (overlay.TenantId != tenantId
                || !string.Equals(overlay.EntityName, entityName, StringComparison.OrdinalIgnoreCase)
                || !overlay.AppliesAt(asOf))
            {
                continue;
            }

            foreach (IFieldMetadata field in overlay.Fields)
            {
                composed.Add(field);
                extended = true;
            }
        }

        return Result.Success<IEntityMetadata>(
            extended
                ? new EntityMetadata(baseEntity.EntityName, baseEntity.TableName, composed)
                : baseEntity);
    }

    private static bool Overlaps(MetadataOverlay first, MetadataOverlay second)
    {
        DateTimeOffset firstEnd = first.EffectiveTo ?? DateTimeOffset.MaxValue;
        DateTimeOffset secondEnd = second.EffectiveTo ?? DateTimeOffset.MaxValue;
        return first.EffectiveFrom < secondEnd && second.EffectiveFrom < firstEnd;
    }
}
