using System;
using System.Collections.Generic;
using Edpf.Abstractions.Data;
using Edpf.Abstractions.Query;
using Edpf.Core.Guards;

namespace Edpf.Data.Query;

/// <summary>
/// A fluent, composable specification builder (Phase 08 §④). Every method
/// takes typed structure — a field name resolved later against metadata, an
/// operator from the closed enum, and values that become parameters. There is
/// deliberately no method that accepts a SQL fragment.
/// </summary>
/// <typeparam name="TEntity">The entity being selected.</typeparam>
public sealed class Specification<TEntity> : ISpecification<TEntity>
    where TEntity : class
{
    private readonly List<SortColumn> _sort = [];
    private readonly List<string> _projection = [];

    /// <inheritdoc />
    public IFilterNode? Filter { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<SortColumn> Sort => _sort;

    /// <inheritdoc />
    public IReadOnlyList<string> Projection => _projection;

    /// <inheritdoc />
    public bool NoTracking { get; private set; } = true;

    /// <inheritdoc />
    public bool IncludeDeleted { get; private set; }

    /// <summary>Starts a new specification.</summary>
    public static Specification<TEntity> Create() => new();

    /// <summary>
    /// Adds a comparison, combined with any existing filter using AND.
    /// </summary>
    /// <param name="fieldName">The field name; resolved against metadata at compile time.</param>
    /// <param name="op">The operator.</param>
    /// <param name="values">Values matching the operator's arity.</param>
    /// <returns>This specification, for chaining.</returns>
    public Specification<TEntity> Where(string fieldName, FilterOperator op, params object?[] values)
    {
        Guard.NotNullOrWhiteSpace(fieldName, nameof(fieldName));
        var comparison = new ComparisonNode(fieldName, op, values ?? []);
        Filter = Filter is null
            ? comparison
            : new CombinationNode(FilterLogic.And, [Filter, comparison]);
        return this;
    }

    /// <summary>
    /// Combines this specification's filter with another node using OR.
    /// </summary>
    /// <param name="alternative">The alternative predicate.</param>
    /// <returns>This specification, for chaining.</returns>
    public Specification<TEntity> Or(IFilterNode alternative)
    {
        Guard.NotNull(alternative, nameof(alternative));
        Filter = Filter is null
            ? alternative
            : new CombinationNode(FilterLogic.Or, [Filter, alternative]);
        return this;
    }

    /// <summary>
    /// Combines this specification's filter with another node using AND.
    /// </summary>
    /// <param name="additional">The additional predicate.</param>
    /// <returns>This specification, for chaining.</returns>
    public Specification<TEntity> And(IFilterNode additional)
    {
        Guard.NotNull(additional, nameof(additional));
        Filter = Filter is null
            ? additional
            : new CombinationNode(FilterLogic.And, [Filter, additional]);
        return this;
    }

    /// <summary>
    /// Adds an ordering column, outermost first.
    /// </summary>
    /// <param name="fieldName">The field to sort by.</param>
    /// <param name="descending">True for descending.</param>
    /// <returns>This specification, for chaining.</returns>
    public Specification<TEntity> OrderBy(string fieldName, bool descending = false)
    {
        _sort.Add(new SortColumn(Guard.NotNullOrWhiteSpace(fieldName, nameof(fieldName)), descending));
        return this;
    }

    /// <summary>
    /// Restricts the projection to the named fields.
    /// </summary>
    /// <param name="fieldNames">The fields to return.</param>
    /// <returns>This specification, for chaining.</returns>
    public Specification<TEntity> Select(params string[] fieldNames)
    {
        Guard.NotNull(fieldNames, nameof(fieldNames));
        _projection.AddRange(fieldNames);
        return this;
    }

    /// <summary>Opts into change tracking, which reads do not need.</summary>
    /// <returns>This specification, for chaining.</returns>
    public Specification<TEntity> WithTracking()
    {
        NoTracking = false;
        return this;
    }

    /// <summary>
    /// Includes soft-deleted rows. An explicit, audited escape — callers must
    /// justify it, and Phase 19 records the access.
    /// </summary>
    /// <param name="auditReason">Why deleted rows are being read. Recorded in the audit trail.</param>
    /// <returns>This specification, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="auditReason"/> is blank.</exception>
    public Specification<TEntity> IncludingDeleted(string auditReason)
    {
        Guard.NotNullOrWhiteSpace(auditReason, nameof(auditReason));
        IncludeDeleted = true;
        DeletedAccessReason = auditReason;
        return this;
    }

    /// <summary>The justification recorded when soft-deleted rows are included.</summary>
    public string? DeletedAccessReason { get; private set; }
}
