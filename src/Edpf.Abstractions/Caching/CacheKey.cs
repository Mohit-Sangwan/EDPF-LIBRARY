using System;
using System.Text;

namespace Edpf.Abstractions.Caching;

/// <summary>
/// A cache key (Phase 15). Like <see cref="Storage.BlobPath"/>, the type
/// exists to make a class of mistake unrepresentable: **a key for a
/// tenant-scoped entity cannot be produced without its tenant**, so two
/// tenants can never collide on one entry.
/// </summary>
/// <remarks>
/// Cache-key collision is one of the twelve routes in the adversarial
/// isolation suite, and it is among the easiest to introduce accidentally —
/// <c>"patient:" + id</c> looks obviously correct and is a cross-tenant leak
/// the moment two tenants share an id space or an id is guessable.
/// </remarks>
public sealed class CacheKey : IEquatable<CacheKey>
{
    /// <summary>Separator between key parts. Rejected inside any part.</summary>
    public const char Separator = ':';

    /// <summary>Prefix identifying a tenant-scoped key.</summary>
    public const char TenantPrefix = 't';

    /// <summary>Prefix identifying a deliberately global key.</summary>
    public const char GlobalPrefix = 'g';

    private CacheKey(string value, Guid? tenantId)
    {
        Value = value;
        TenantId = tenantId;
    }

    /// <summary>The rendered key.</summary>
    public string Value { get; }

    /// <summary>The owning tenant, or null for an explicitly global key.</summary>
    public Guid? TenantId { get; }

    /// <summary>True when this key is scoped to a tenant.</summary>
    public bool IsTenantScoped => TenantId.HasValue;

    /// <summary>
    /// Creates a tenant-scoped key. The tenant is the first component, so
    /// every entry is partitioned by construction.
    /// </summary>
    /// <param name="tenantId">The owning tenant. Must not be empty.</param>
    /// <param name="entityName">Entity or cache-region name.</param>
    /// <param name="parts">Further discriminators — an id, a query hash.</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentException">
    /// The tenant is empty, the entity name is blank, or any part contains
    /// the separator or a control character.
    /// </exception>
    public static CacheKey ForTenant(Guid tenantId, string entityName, params string[] parts)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException(
                "A tenant-scoped cache key requires a tenant; an unprefixed key is not constructible.",
                nameof(tenantId));
        }

        ValidatePart(entityName, nameof(entityName));

        var builder = new StringBuilder()
            .Append(TenantPrefix).Append(Separator)
            .Append(tenantId.ToString("N")).Append(Separator)
            .Append(entityName);

        AppendParts(builder, parts);

        return new CacheKey(builder.ToString(), tenantId);
    }

    /// <summary>
    /// Creates a deliberately global key — for data that genuinely belongs to
    /// no tenant, such as a currency table or a terminology set.
    /// </summary>
    /// <param name="justification">
    /// Why this data is not tenant-scoped. Required: a global key is the one
    /// way to cache across the boundary, so choosing it must be conscious.
    /// </param>
    /// <param name="entityName">Entity or cache-region name.</param>
    /// <param name="parts">Further discriminators.</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentException"><paramref name="justification"/> is blank, or a part is invalid.</exception>
    public static CacheKey Global(string justification, string entityName, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(justification))
        {
            throw new ArgumentException(
                "A global cache key requires a written justification; it is the only key shape that "
                + "crosses the tenant boundary.",
                nameof(justification));
        }

        ValidatePart(entityName, nameof(entityName));

        var builder = new StringBuilder()
            .Append(GlobalPrefix).Append(Separator)
            .Append(entityName);

        AppendParts(builder, parts);

        return new CacheKey(builder.ToString(), tenantId: null);
    }

    /// <summary>
    /// True when this key may be read by the given tenant. A global key is
    /// readable by all; a tenant key only by its owner.
    /// </summary>
    /// <param name="tenantId">The reading tenant.</param>
    public bool IsReadableBy(Guid tenantId) => !IsTenantScoped || TenantId == tenantId;

    private static void AppendParts(StringBuilder builder, string[]? parts)
    {
        if (parts is null)
        {
            return;
        }

        foreach (string part in parts)
        {
            ValidatePart(part, nameof(parts));
            builder.Append(Separator).Append(part);
        }
    }

    private static void ValidatePart(string part, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(part))
        {
            throw new ArgumentException("Cache key parts must not be blank.", parameterName);
        }

        foreach (char c in part)
        {
            if (c == Separator || char.IsControl(c) || c == '*' || c == '?')
            {
                throw new ArgumentException(
                    "Cache key parts must not contain the separator, a control character, or a glob "
                    + "wildcard — any of which would let one key address another's entries.",
                    parameterName);
            }
        }
    }

    /// <inheritdoc />
    public bool Equals(CacheKey? other)
        => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as CacheKey);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <summary>The rendered key.</summary>
    public override string ToString() => Value;
}
