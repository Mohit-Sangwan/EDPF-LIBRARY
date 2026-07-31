using System;

namespace Edpf.Abstractions.Security;

/// <summary>
/// Identifies whose key material an operation uses (ADR-004, ADR-006):
/// tenant-scoped, or subject-scoped within a tenant. Subject scope is what
/// makes crypto-shredding per data subject possible — destroy the subject DEK
/// and only that subject's data becomes unrecoverable.
/// </summary>
public readonly struct KeyScope : IEquatable<KeyScope>
{
    private KeyScope(Guid tenantId, Guid? subjectId)
    {
        TenantId = tenantId;
        SubjectId = subjectId;
    }

    /// <summary>The tenant whose KEK wraps the resolved DEK.</summary>
    public Guid TenantId { get; }

    /// <summary>The data subject, when the scope is subject-level; null for tenant scope.</summary>
    public Guid? SubjectId { get; }

    /// <summary>True when this scope is subject-level.</summary>
    public bool IsSubjectScoped => SubjectId.HasValue;

    /// <summary>
    /// A tenant-scoped key scope.
    /// </summary>
    /// <param name="tenantId">The tenant. Must not be empty.</param>
    /// <exception cref="ArgumentException"><paramref name="tenantId"/> is empty.</exception>
    public static KeyScope ForTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        return new KeyScope(tenantId, null);
    }

    /// <summary>
    /// A subject-scoped key scope within a tenant (ADR-006 crypto-shredding unit).
    /// </summary>
    /// <param name="tenantId">The tenant. Must not be empty.</param>
    /// <param name="subjectId">The data subject. Must not be empty.</param>
    /// <exception cref="ArgumentException">Either id is empty.</exception>
    public static KeyScope ForSubject(Guid tenantId, Guid subjectId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        if (subjectId == Guid.Empty)
        {
            throw new ArgumentException("Subject id must not be empty.", nameof(subjectId));
        }

        return new KeyScope(tenantId, subjectId);
    }

    /// <inheritdoc />
    public bool Equals(KeyScope other)
        => TenantId.Equals(other.TenantId) && Nullable.Equals(SubjectId, other.SubjectId);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is KeyScope other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        unchecked
        {
            return (TenantId.GetHashCode() * 397) ^ (SubjectId?.GetHashCode() ?? 0);
        }
    }

    /// <summary>Value equality.</summary>
    public static bool operator ==(KeyScope left, KeyScope right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(KeyScope left, KeyScope right) => !left.Equals(right);

    /// <summary>Scope ids only — contains no key material; safe to log.</summary>
    public override string ToString()
        => IsSubjectScoped
            ? "tenant " + TenantId.ToString("D") + " / subject " + SubjectId!.Value.ToString("D")
            : "tenant " + TenantId.ToString("D");
}
