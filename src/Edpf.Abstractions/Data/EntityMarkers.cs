using System;

namespace Edpf.Abstractions.Data;

/// <summary>
/// An entity that belongs to exactly one tenant (§10.1 Repository).
/// Rule EDPF0009: any repository query over a tenant-scoped entity requires a
/// resolved tenant context; <c>TenantId</c> leads every clustered index (Z.2).
/// </summary>
public interface ITenantScopedEntity
{
    /// <summary>The owning tenant. Immutable after creation.</summary>
    Guid TenantId { get; }
}

/// <summary>
/// An entity carrying creation/modification instants (§10.1 Repository).
/// Timestamps come from <see cref="Primitives.IClock"/>, never from
/// <see cref="DateTime.UtcNow"/> (Z.3 rule 4).
/// </summary>
public interface IAuditableEntity
{
    /// <summary>When the entity was created (UTC).</summary>
    DateTimeOffset CreatedUtc { get; }

    /// <summary>When the entity was last modified (UTC), if ever.</summary>
    DateTimeOffset? ModifiedUtc { get; }
}
