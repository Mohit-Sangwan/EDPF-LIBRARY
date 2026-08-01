using System;
using System.Collections.Generic;
using System.Text;

namespace Edpf.Abstractions.Identity;

/// <summary>The levels of the organisational hierarchy (Phase 21).</summary>
/// <remarks>
/// The original specification listed "Department Security", "Hospital
/// Security" and "Facility Security" as separate features. They are one
/// model at different depths; unifying them removes three parallel
/// implementations and the inconsistencies that always develop between them.
/// </remarks>
public enum ScopeLevel
{
    /// <summary>The whole organisation.</summary>
    Organization = 0,

    /// <summary>A facility or hospital.</summary>
    Facility = 1,

    /// <summary>A department within a facility.</summary>
    Department = 2,

    /// <summary>A unit or ward within a department.</summary>
    Unit = 3,

    /// <summary>A single resource.</summary>
    Resource = 4,
}

/// <summary>
/// A position in the organisational hierarchy that a grant applies to
/// (Phase 21). Authority at one level implies authority below it and never
/// above or across.
/// </summary>
public sealed class AuthorizationScope : IEquatable<AuthorizationScope>
{
    private readonly string[] _segments;

    private AuthorizationScope(Guid tenantId, string[] segments)
    {
        TenantId = tenantId;
        _segments = segments;
        Level = (ScopeLevel)(segments.Length - 1);
        Value = tenantId.ToString("N") + "/" + string.Join("/", segments);
    }

    /// <summary>The tenant this scope lives in. Scopes never span tenants.</summary>
    public Guid TenantId { get; }

    /// <summary>How deep the scope reaches.</summary>
    public ScopeLevel Level { get; }

    /// <summary>The rendered scope path.</summary>
    public string Value { get; }

    /// <summary>
    /// Creates a scope from its path segments, outermost first.
    /// </summary>
    /// <param name="tenantId">The owning tenant. Must not be empty.</param>
    /// <param name="segments">
    /// One to five segments: organisation, facility, department, unit,
    /// resource.
    /// </param>
    /// <returns>The scope.</returns>
    /// <exception cref="ArgumentException">
    /// The tenant is empty, the segment count is outside 1..5, or a segment
    /// is blank or contains a separator or wildcard.
    /// </exception>
    public static AuthorizationScope Create(Guid tenantId, params string[] segments)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException(
                "An authorization scope requires a tenant; scopes never span tenants.", nameof(tenantId));
        }

        if (segments is null || segments.Length is < 1 or > 5)
        {
            throw new ArgumentException(
                "A scope has between one and five segments: organization, facility, department, unit, resource.",
                nameof(segments));
        }

        foreach (string segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                throw new ArgumentException("Scope segments must not be blank.", nameof(segments));
            }

            foreach (char c in segment)
            {
                if (c is '/' or '*' or '?' || char.IsControl(c))
                {
                    throw new ArgumentException(
                        "Scope segments must not contain a separator or wildcard — either would let one grant "
                        + "match scopes it was not given.",
                        nameof(segments));
                }
            }
        }

        return new AuthorizationScope(tenantId, (string[])segments.Clone());
    }

    /// <summary>
    /// True when authority over this scope implies authority over
    /// <paramref name="other"/> — that is, when <paramref name="other"/> is
    /// this scope or lies beneath it.
    /// </summary>
    /// <param name="other">The scope being reached for.</param>
    /// <remarks>
    /// Containment is prefix-based **on whole segments**, so
    /// <c>department/cardio</c> does not contain <c>department/cardiology</c>.
    /// A substring check here would silently widen every grant whose name is a
    /// prefix of another's.
    /// </remarks>
    public bool Contains(AuthorizationScope other)
    {
        if (other is null || other.TenantId != TenantId)
        {
            return false;
        }

        if (other._segments.Length < _segments.Length)
        {
            return false;
        }

        for (int i = 0; i < _segments.Length; i++)
        {
            if (!string.Equals(_segments[i], other._segments[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The scope one level up, or null at the organisation level.</summary>
    public AuthorizationScope? Parent()
    {
        if (_segments.Length <= 1)
        {
            return null;
        }

        // Explicit copy rather than a range expression: System.Index and
        // System.Range do not exist on Tier 3 TFMs (ADR-002).
        var parentSegments = new string[_segments.Length - 1];
        Array.Copy(_segments, parentSegments, parentSegments.Length);
        return new AuthorizationScope(TenantId, parentSegments);
    }

    /// <summary>The segments, outermost first.</summary>
    public IReadOnlyList<string> Segments => _segments;

    /// <inheritdoc />
    public bool Equals(AuthorizationScope? other)
        => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as AuthorizationScope);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <summary>The rendered scope path.</summary>
    public override string ToString() => Value;
}

/// <summary>
/// A decision made by the authorization pipeline (Phase 21). Every decision —
/// **including every denial** — is auditable, because a denial is the record
/// that shows a control worked.
/// </summary>
public sealed class AuthorizationDecision
{
    private AuthorizationDecision(bool allowed, string requiredPermission, AuthorizationScope? scope, string reason)
    {
        IsAllowed = allowed;
        RequiredPermission = requiredPermission;
        Scope = scope;
        Reason = reason;
    }

    /// <summary>True when the operation is permitted.</summary>
    public bool IsAllowed { get; }

    /// <summary>The permission that was evaluated.</summary>
    public string RequiredPermission { get; }

    /// <summary>The scope evaluated against, if any.</summary>
    public AuthorizationScope? Scope { get; }

    /// <summary>
    /// Why the decision went the way it did. Written for the audit trail and
    /// for a support engineer — **never returned to the caller**, who receives
    /// only the required permission (§10.2 EDPF-AUTHZ-2101).
    /// </summary>
    public string Reason { get; }

    /// <summary>Allows the operation.</summary>
    /// <param name="permission">The permission granted.</param>
    /// <param name="scope">The scope it was granted within.</param>
    /// <param name="reason">Why, for the audit trail.</param>
    public static AuthorizationDecision Allow(string permission, AuthorizationScope? scope, string reason)
        => new(true, permission, scope, reason);

    /// <summary>Denies the operation.</summary>
    /// <param name="permission">The permission that was missing.</param>
    /// <param name="scope">The scope evaluated.</param>
    /// <param name="reason">Why, for the audit trail.</param>
    public static AuthorizationDecision Deny(string permission, AuthorizationScope? scope, string reason)
        => new(false, permission, scope, reason);
}
