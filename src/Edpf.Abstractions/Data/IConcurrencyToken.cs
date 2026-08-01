using System;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Data;

/// <summary>
/// An entity's version, used for optimistic concurrency (ADR-020). Rendered
/// per provider as <c>rowversion</c>, <c>xmin</c> or an ETag.
/// </summary>
public interface IConcurrencyToken
{
    /// <summary>
    /// The opaque version value. Callers round-trip it; they never interpret
    /// it, because its meaning differs per provider.
    /// </summary>
    byte[] Version { get; }
}

/// <summary>What to do when two writers collide (ADR-020).</summary>
public enum ConcurrencyStrategy
{
    /// <summary>
    /// Surface the conflict as <see cref="ErrorCodes.ConcurrencyConflict"/>.
    /// **The default.** In a clinical or financial system a silently lost
    /// update is a patient-safety or audit problem, so the caller is always
    /// told.
    /// </summary>
    Fail = 0,

    /// <summary>
    /// The later write wins. Never the default, and never implicit: choosing
    /// it is a documented decision about data that can be safely overwritten.
    /// </summary>
    LastWriteWins = 1,

    /// <summary>Delegate to an <see cref="IConflictResolver{T}"/>.</summary>
    Merge = 2,
}

/// <summary>
/// Resolves a concurrency conflict for one entity type (ADR-020). Registering
/// a resolver is how a domain opts into anything other than failing.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public interface IConflictResolver<TEntity>
    where TEntity : class
{
    /// <summary>
    /// Merges a rejected write with the current stored state.
    /// </summary>
    /// <param name="attempted">What the caller tried to write.</param>
    /// <param name="current">What is actually stored now.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <returns>
    /// The merged entity to retry with, or failure to surface the conflict.
    /// A resolver that cannot merge safely **must** fail rather than guess.
    /// </returns>
    Task<Result<TEntity>> ResolveAsync(TEntity attempted, TEntity current, CancellationToken cancellationToken);
}

/// <summary>An entity that is soft-deleted rather than removed.</summary>
public interface ISoftDeletable
{
    /// <summary>True when the row is soft-deleted and excluded from normal reads.</summary>
    bool IsDeleted { get; }

    /// <summary>When it was soft-deleted.</summary>
    DateTimeOffset? DeletedUtc { get; }
}
