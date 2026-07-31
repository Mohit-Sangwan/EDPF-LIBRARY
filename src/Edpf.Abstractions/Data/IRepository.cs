using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Data;

/// <summary>
/// Read-side repository contract (§10.1 Repository). Every implementation is
/// tenant-scoped: the tenant filter is unavoidable (rule EDPF0009), applied by
/// the repository, never left to caller discipline.
/// </summary>
/// <typeparam name="TEntity">The aggregate root type.</typeparam>
/// <typeparam name="TKey">The identifier type.</typeparam>
public interface IReadRepository<TEntity, in TKey>
    where TEntity : class
{
    /// <summary>
    /// Loads an entity by id within the current tenant.
    /// </summary>
    /// <param name="id">The entity id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The entity, or failure with <see cref="ErrorCodes.NotFound"/> — which
    /// deliberately covers both "absent" and "belongs to another tenant".
    /// </returns>
    Task<Result<TEntity>> GetByIdAsync(TKey id, CancellationToken cancellationToken);

    /// <summary>
    /// Lists entities within the current tenant, paged.
    /// </summary>
    /// <param name="page">The page to fetch.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The requested page.</returns>
    Task<Result<PagedResult<TEntity>>> ListAsync(PageRequest page, CancellationToken cancellationToken);
}

/// <summary>
/// Full repository contract (§10.1 Repository): reads plus mutation, always
/// through the unit of work — repositories never commit on their own.
/// </summary>
/// <typeparam name="TEntity">The aggregate root type.</typeparam>
/// <typeparam name="TKey">The identifier type.</typeparam>
public interface IRepository<TEntity, in TKey> : IReadRepository<TEntity, TKey>
    where TEntity : class
{
    /// <summary>
    /// Stages a new entity for insertion in the current tenant.
    /// </summary>
    /// <param name="entity">The entity to add.</param>
    /// <param name="cancellationToken">Cancels the staging.</param>
    /// <returns>The staged entity, or failure (e.g. <see cref="ErrorCodes.Duplicate"/>).</returns>
    Task<Result<TEntity>> AddAsync(TEntity entity, CancellationToken cancellationToken);
}
