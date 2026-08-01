using System;
using System.Collections.Generic;
using System.Linq;

namespace Edpf.Abstractions.Query;

/// <summary>
/// A comparison of one field against one or more values (ADR-018).
/// </summary>
/// <remarks>
/// The field name is resolved against entity metadata before a node is
/// constructed, and the operator comes from a closed enum. Values are held as
/// objects and are **never** rendered into SQL — the compiler emits a
/// parameter placeholder for each. That is the structural property that makes
/// injection impossible rather than merely unlikely.
/// </remarks>
public sealed class ComparisonNode : IFilterNode
{
    /// <summary>
    /// Initializes a comparison.
    /// </summary>
    /// <param name="fieldName">Metadata-resolved field name.</param>
    /// <param name="op">The operator.</param>
    /// <param name="values">
    /// Values for the operator: none for null checks, one for most, two for
    /// <see cref="FilterOperator.Between"/>, and a bounded set for
    /// <see cref="FilterOperator.In"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The field name is blank, or the value count does not match the
    /// operator's arity.
    /// </exception>
    public ComparisonNode(string fieldName, FilterOperator op, IReadOnlyList<object?> values)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            throw new ArgumentException("Field name must not be blank.", nameof(fieldName));
        }

        if (values is null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        ValidateArity(op, values.Count);

        FieldName = fieldName;
        Operator = op;
        Values = values;
    }

    /// <summary>The metadata-resolved field name.</summary>
    public string FieldName { get; }

    /// <summary>The operator, from the closed enum.</summary>
    public FilterOperator Operator { get; }

    /// <summary>The comparison values. Always parameterised, never rendered.</summary>
    public IReadOnlyList<object?> Values { get; }

    /// <inheritdoc />
    public TResult Accept<TResult>(IFilterVisitor<TResult> visitor)
    {
        if (visitor is null)
        {
            throw new ArgumentNullException(nameof(visitor));
        }

        return visitor.VisitComparison(this);
    }

    private static void ValidateArity(FilterOperator op, int valueCount)
    {
        switch (op)
        {
            case FilterOperator.IsNull:
            case FilterOperator.IsNotNull:
                if (valueCount != 0)
                {
                    throw new ArgumentException($"{op} takes no values.", nameof(valueCount));
                }

                break;

            case FilterOperator.Between:
                if (valueCount != 2)
                {
                    throw new ArgumentException("Between takes exactly two values.", nameof(valueCount));
                }

                break;

            case FilterOperator.In:
            case FilterOperator.NotIn:
                if (valueCount == 0)
                {
                    throw new ArgumentException($"{op} requires at least one value.", nameof(valueCount));
                }

                break;

            default:
                if (valueCount != 1)
                {
                    throw new ArgumentException($"{op} takes exactly one value.", nameof(valueCount));
                }

                break;
        }
    }
}

/// <summary>Combines child nodes with AND or OR.</summary>
public sealed class CombinationNode : IFilterNode
{
    /// <summary>
    /// Initializes a combination.
    /// </summary>
    /// <param name="logic">How the children combine.</param>
    /// <param name="children">The child nodes; at least one.</param>
    /// <exception cref="ArgumentException"><paramref name="children"/> is empty.</exception>
    public CombinationNode(FilterLogic logic, IReadOnlyList<IFilterNode> children)
    {
        if (children is null)
        {
            throw new ArgumentNullException(nameof(children));
        }

        if (children.Count == 0)
        {
            throw new ArgumentException("A combination requires at least one child.", nameof(children));
        }

        if (children.Any(c => c is null))
        {
            throw new ArgumentException("Child nodes must not be null.", nameof(children));
        }

        Logic = logic;
        Children = children;
    }

    /// <summary>How the children combine.</summary>
    public FilterLogic Logic { get; }

    /// <summary>The child nodes.</summary>
    public IReadOnlyList<IFilterNode> Children { get; }

    /// <inheritdoc />
    public TResult Accept<TResult>(IFilterVisitor<TResult> visitor)
    {
        if (visitor is null)
        {
            throw new ArgumentNullException(nameof(visitor));
        }

        return visitor.VisitCombination(this);
    }
}
