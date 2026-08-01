using System;
using System.Text;

namespace Edpf.Abstractions.Storage;

/// <summary>
/// A tenant-scoped blob path (Phase 14 §④). The type exists so that
/// **cross-tenant blob paths are unconstructable**: there is no public way to
/// obtain a <see cref="BlobPath"/> without supplying the tenant it belongs
/// to, and the rendered path always begins with that tenant's segment.
/// </summary>
/// <remarks>
/// <para>
/// Traversal is rejected rather than normalised. A caller-supplied
/// <c>..</c>, absolute prefix, alternate separator, encoded separator, or NUL
/// byte fails construction — the value did not come from anywhere legitimate,
/// so cleaning it up would only hide the problem.
/// </para>
/// <para>
/// This is what makes the isolation suite's blob route defensible: the test
/// cannot construct a hostile path, because the type will not produce one.
/// </para>
/// </remarks>
public sealed class BlobPath : IEquatable<BlobPath>
{
    /// <summary>Maximum length of a single path segment.</summary>
    public const int MaxSegmentLength = 255;

    /// <summary>Maximum total path length.</summary>
    public const int MaxPathLength = 1024;

    private BlobPath(Guid tenantId, string relativePath)
    {
        TenantId = tenantId;
        RelativePath = relativePath;
        Value = "tenants/" + tenantId.ToString("D") + "/" + relativePath;
    }

    /// <summary>The owning tenant. Always the first path segment.</summary>
    public Guid TenantId { get; }

    /// <summary>The path below the tenant root.</summary>
    public string RelativePath { get; }

    /// <summary>The full storage path, always tenant-prefixed.</summary>
    public string Value { get; }

    /// <summary>
    /// Creates a tenant-scoped path.
    /// </summary>
    /// <param name="tenantId">The owning tenant. Must not be empty.</param>
    /// <param name="segments">
    /// Path segments below the tenant root. Each is validated
    /// independently — a separator inside a segment is a rejection, not a
    /// nested path.
    /// </param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentException">
    /// The tenant is empty, no segments were supplied, or any segment is
    /// blank, over-long, traversal (<c>.</c> / <c>..</c>), or contains a
    /// separator, NUL, control character, or percent-encoding.
    /// </exception>
    public static BlobPath Create(Guid tenantId, params string[] segments)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException(
                "A blob path requires a tenant; an unscoped path is not constructible.", nameof(tenantId));
        }

        if (segments is null || segments.Length == 0)
        {
            throw new ArgumentException("A blob path requires at least one segment.", nameof(segments));
        }

        var builder = new StringBuilder();
        for (int i = 0; i < segments.Length; i++)
        {
            ValidateSegment(segments[i], nameof(segments));

            if (i > 0)
            {
                builder.Append('/');
            }

            builder.Append(segments[i]);
        }

        if (builder.Length + 46 > MaxPathLength)
        {
            throw new ArgumentException("The resulting blob path exceeds the maximum length.", nameof(segments));
        }

        return new BlobPath(tenantId, builder.ToString());
    }

    /// <summary>
    /// True when <paramref name="path"/> belongs to <paramref name="tenantId"/>.
    /// Storage adapters call this before every operation, so a path that
    /// somehow arrived from elsewhere is still refused at the boundary.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <param name="tenantId">The tenant the caller is acting as.</param>
    public static bool BelongsTo(BlobPath path, Guid tenantId)
        => path is not null && path.TenantId == tenantId;

    private static void ValidateSegment(string segment, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            throw new ArgumentException("Path segments must not be blank.", parameterName);
        }

        if (segment.Length > MaxSegmentLength)
        {
            throw new ArgumentException("Path segment exceeds the maximum length.", parameterName);
        }

        if (segment == "." || segment == "..")
        {
            throw new ArgumentException(
                "Traversal segments are rejected, not normalised.", parameterName);
        }

        foreach (char c in segment)
        {
            bool illegal = c is '/' or '\\' or '\0' or ':' or '%' || char.IsControl(c);
            if (illegal)
            {
                throw new ArgumentException(
                    "Path segment contains a separator, control character or encoding marker. "
                    + "Segments are validated individually so a separator cannot create structure.",
                    parameterName);
            }
        }
    }

    /// <inheritdoc />
    public bool Equals(BlobPath? other)
        => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as BlobPath);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <summary>The full tenant-prefixed path.</summary>
    public override string ToString() => Value;
}
