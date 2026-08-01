using System;
using System.Collections.Generic;
using Edpf.Abstractions.Metadata;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;
using Edpf.Core.Guards;

namespace Edpf.Metadata;

/// <summary>
/// Answers, for any field of any entity, what protections it requires
/// (Phase 05b verification).
/// </summary>
/// <remarks>
/// <para>
/// This is the type the phase's central claim rests on: **a field defined at
/// runtime receives encryption, redaction, audit and subject-access inclusion
/// with no code written.** It does so because every subsystem asks this
/// resolver, the resolver reads
/// <see cref="IFieldMetadata.Classification"/>, and classification is present
/// on a runtime-defined field for exactly the same reason it is present on a
/// compiled one — someone declared it.
/// </para>
/// <para>
/// No method here inspects <see cref="IFieldMetadata.IsRuntimeDefined"/>.
/// That is deliberate and load-bearing, and there is an architecture test that
/// says so.
/// </para>
/// </remarks>
public sealed class MetadataProtectionResolver
{
    private readonly IMetadataRepository _repository;
    private readonly IDataProtectionPolicy _policy;

    /// <summary>Initializes the resolver.</summary>
    /// <param name="repository">The metadata source.</param>
    /// <param name="policy">The classification-to-protection policy.</param>
    public MetadataProtectionResolver(IMetadataRepository repository, IDataProtectionPolicy? policy = null)
    {
        _repository = Guard.NotNull(repository, nameof(repository));
        _policy = policy ?? ProtectionPolicy.Default;
    }

    /// <summary>
    /// Returns the protections a single field requires.
    /// </summary>
    /// <param name="entityName">The entity.</param>
    /// <param name="fieldName">The field.</param>
    /// <param name="tenantId">The tenant whose overlay applies.</param>
    /// <param name="asOf">The point in time to resolve metadata at.</param>
    /// <returns>The requirements, or a failure if the field is not declared.</returns>
    public Result<DataProtectionRequirements> ForField(
        string entityName, string fieldName, Guid tenantId, DateTimeOffset asOf)
    {
        Result<IEntityMetadata> entity = _repository.GetEntity(entityName, tenantId, asOf);
        if (entity.IsFailure)
        {
            return Result.Failure<DataProtectionRequirements>(entity.Error!);
        }

        Result<IFieldMetadata> field = entity.Value.ResolveField(fieldName);
        return field.IsFailure
            ? Result.Failure<DataProtectionRequirements>(field.Error!)
            : Result.Success(_policy.For(field.Value.Classification));
    }

    /// <summary>
    /// Returns every field of an entity that requires <paramref name="requirement"/>.
    /// </summary>
    /// <param name="entityName">The entity.</param>
    /// <param name="requirement">The protection to select on.</param>
    /// <param name="tenantId">The tenant whose overlay applies.</param>
    /// <param name="asOf">The point in time to resolve metadata at.</param>
    /// <returns>The matching field names, or a failure if the entity is unknown.</returns>
    /// <remarks>
    /// This is what a storage layer calls to know what to encrypt, what an
    /// export builder calls to know what a subject-access request must
    /// contain, and what an audit interceptor calls to know which reads to
    /// record. One question, asked three times, answered the same way.
    /// </remarks>
    public Result<IReadOnlyList<string>> FieldsRequiring(
        string entityName, DataProtectionRequirements requirement, Guid tenantId, DateTimeOffset asOf)
    {
        Result<IEntityMetadata> entity = _repository.GetEntity(entityName, tenantId, asOf);
        if (entity.IsFailure)
        {
            return Result.Failure<IReadOnlyList<string>>(entity.Error!);
        }

        var matching = new List<string>();

        foreach (KeyValuePair<string, IFieldMetadata> pair in entity.Value.Fields)
        {
            if (_policy.For(pair.Value.Classification).HasFlagSet(requirement))
            {
                matching.Add(pair.Value.Name);
            }
        }

        matching.Sort(StringComparer.Ordinal);
        return Result.Success<IReadOnlyList<string>>(matching);
    }

    /// <summary>
    /// Produces a diagnostics-safe view of a dynamic entity.
    /// </summary>
    /// <param name="entity">The instance.</param>
    /// <param name="asOf">The point in time to resolve metadata at.</param>
    /// <returns>Field names mapped to either their value or a redaction marker.</returns>
    /// <remarks>
    /// The redaction marker replaces the value without revealing its length,
    /// type or presence pattern — a marker that varied with the value would
    /// leak the value it was hiding.
    /// </remarks>
    public IReadOnlyDictionary<string, object?> RedactForDiagnostics(
        IDynamicEntity entity, DateTimeOffset asOf)
    {
        Guard.NotNull(entity, nameof(entity));
        _ = asOf;

        var view = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (KeyValuePair<string, IFieldMetadata> pair in entity.Metadata.Fields)
        {
            IFieldMetadata field = pair.Value;

            if (_policy.For(field.Classification).HasFlagSet(DataProtectionRequirements.RedactInDiagnostics))
            {
                view[field.Name] = RedactionMarker;
                continue;
            }

            Result<object?> value = entity.GetValue(field.Name);
            view[field.Name] = value.IsSuccess ? value.Value : null;
        }

        return view;
    }

    /// <summary>The marker substituted for a redacted value.</summary>
    public const string RedactionMarker = "[REDACTED]";
}
