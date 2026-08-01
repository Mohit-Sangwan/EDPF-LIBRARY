using System.Collections.Generic;
using Edpf.Abstractions.Data;

namespace Edpf.Abstractions.Query;

/// <summary>
/// A composable, provider-neutral query description (Phase 08 §④). A
/// specification carries **structure** — which fields, which operators, which
/// ordering — and values travel separately as parameters, so nothing a caller
/// supplies can become SQL (ADR-018).
/// </summary>
/// <typeparam name="TEntity">The entity the specification selects.</typeparam>
public interface ISpecification<TEntity>
    where TEntity : class
{
    /// <summary>The filter tree, or null to select all rows within tenant scope.</summary>
    IFilterNode? Filter { get; }

    /// <summary>
    /// Ordering, outermost first. Always non-empty in practice: an unstable
    /// sort silently duplicates and drops rows across pages (BRL-017), so
    /// implementations append a unique tiebreaker.
    /// </summary>
    IReadOnlyList<SortColumn> Sort { get; }

    /// <summary>
    /// Fields to project, or empty for the full entity. Field-level
    /// authorization may strip entries from this list (EDPF-AUTHZ-2103).
    /// </summary>
    IReadOnlyList<string> Projection { get; }

    /// <summary>True when the read should bypass change tracking (the default for reads).</summary>
    bool NoTracking { get; }

    /// <summary>
    /// True to include soft-deleted rows. Setting it is an explicit, audited
    /// escape (Phase 10 §④) — never the default.
    /// </summary>
    bool IncludeDeleted { get; }
}

/// <summary>A node in a filter tree: either a comparison or a combination.</summary>
public interface IFilterNode
{
    /// <summary>
    /// Walks the tree.
    /// </summary>
    /// <typeparam name="TResult">What the visitor produces.</typeparam>
    /// <param name="visitor">The visitor.</param>
    /// <returns>The visitor's result for this node.</returns>
    TResult Accept<TResult>(IFilterVisitor<TResult> visitor);
}

/// <summary>Visits the two node kinds. Closed by design — no third kind can be added by a caller.</summary>
/// <typeparam name="TResult">What the visitor produces.</typeparam>
public interface IFilterVisitor<out TResult>
{
    /// <summary>Visits a field comparison.</summary>
    /// <param name="node">The comparison node.</param>
    TResult VisitComparison(ComparisonNode node);

    /// <summary>Visits a logical combination.</summary>
    /// <param name="node">The combination node.</param>
    TResult VisitCombination(CombinationNode node);
}
