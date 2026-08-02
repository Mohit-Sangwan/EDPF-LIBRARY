using System;
using System.Collections.Generic;

namespace Edpf.Abstractions.Identity;

/// <summary>
/// The field-level permissions a caller holds (Phase 08b).
/// </summary>
/// <remarks>
/// Separate from <see cref="AuthorizationScope"/>, which answers *"which part
/// of the organisation may this caller act in"*. This answers *"which
/// protected fields may this caller see"*, and the two are independent: a
/// ward clerk may be scoped to the whole hospital and still have no business
/// reading a national identifier.
/// </remarks>
public interface IFieldPermissions
{
    /// <summary>
    /// Whether the caller holds <paramref name="requiredPermission"/>.
    /// </summary>
    /// <param name="requiredPermission">The permission a field declares.</param>
    /// <returns>Whether the caller holds it.</returns>
    bool Grants(string requiredPermission);
}

/// <summary>
/// A caller's granted field permissions (Phase 08b).
/// </summary>
/// <remarks>
/// <para>
/// **Membership is an exact ordinal match. There is deliberately no prefix
/// matching.**
/// </para>
/// <para>
/// Prefix matching on permission strings is a classic and quiet
/// vulnerability: a caller granted <c>patient.read</c> would satisfy a field
/// requiring <c>patient.readAll</c>, and a caller granted <c>admin</c> would
/// satisfy everything beginning with those five letters. The bug is invisible
/// in review because the grant looks narrower than the requirement.
/// </para>
/// <para>
/// If a hierarchy is genuinely needed, it belongs in
/// <see cref="AuthorizationScope"/>, which compares segment-by-segment and
/// cannot be fooled by a shared prefix.
/// </para>
/// </remarks>
public sealed class FieldPermissionSet : IFieldPermissions
{
    private readonly HashSet<string> _granted;

    /// <summary>Initializes a permission set.</summary>
    /// <param name="granted">The permissions the caller holds.</param>
    public FieldPermissionSet(IEnumerable<string> granted)
    {
        if (granted is null)
        {
            throw new ArgumentNullException(nameof(granted));
        }

        _granted = new HashSet<string>(StringComparer.Ordinal);

        foreach (string permission in granted)
        {
            if (!string.IsNullOrWhiteSpace(permission))
            {
                _granted.Add(permission);
            }
        }
    }

    /// <summary>
    /// A caller holding nothing.
    /// </summary>
    /// <remarks>
    /// The value a compiler uses when no permissions are supplied. Named
    /// rather than null so that "no permissions were provided" and "the caller
    /// holds none" are the same thing at the point of decision — the safe
    /// direction, since a forgotten argument then denies rather than
    /// discloses.
    /// </remarks>
    public static FieldPermissionSet None { get; } = new(Array.Empty<string>());

    /// <inheritdoc />
    public bool Grants(string requiredPermission)
        => !string.IsNullOrWhiteSpace(requiredPermission) && _granted.Contains(requiredPermission);

    /// <summary>The permissions held, for diagnostics.</summary>
    public IReadOnlyCollection<string> Granted => _granted;
}
