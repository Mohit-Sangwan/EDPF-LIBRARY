using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Edpf.Abstractions.Data;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;
using Edpf.Core.Guards;

namespace Edpf.Data.Query;

/// <summary>
/// Compiles a filter tree into SQL (ADR-018). This is the security-critical
/// class in the framework, and its design property is simple: **no caller
/// string ever reaches the output.**
/// </summary>
/// <remarks>
/// <para>Three rules hold at every node:</para>
/// <list type="number">
/// <item>Field names are resolved through <see cref="IEntityMetadata"/> and
/// rendered through the dialect's identifier quoting, which rejects anything
/// illegal rather than escaping it.</item>
/// <item>Operators come from a closed enum, mapped to fixed SQL text by a
/// <c>switch</c> — there is no code path where an operator is a string.</item>
/// <item>Values are never rendered. Each becomes a framework-named parameter
/// (<c>p0</c>, <c>p1</c>, …) and travels in the parameter dictionary.</item>
/// </list>
/// <para>
/// The consequence is that injection is not defended against; it is
/// unrepresentable. An attacker controlling every value in the tree still
/// produces byte-identical SQL, and the injection corpus asserts exactly that.
/// </para>
/// </remarks>
public sealed class FilterCompiler : IFilterVisitor<Result<string>>
{
    private readonly ISqlDialect _dialect;
    private readonly IEntityMetadata _metadata;
    private readonly Dictionary<string, object?> _parameters = new(StringComparer.Ordinal);
    private int _parameterIndex;

    /// <summary>
    /// Initializes a compiler for one entity against one dialect.
    /// </summary>
    /// <param name="dialect">The target dialect.</param>
    /// <param name="metadata">The entity's field metadata.</param>
    public FilterCompiler(ISqlDialect dialect, IEntityMetadata metadata)
    {
        _dialect = Guard.NotNull(dialect, nameof(dialect));
        _metadata = Guard.NotNull(metadata, nameof(metadata));
    }

    /// <summary>Parameters accumulated while compiling.</summary>
    public IReadOnlyDictionary<string, object?> Parameters => _parameters;

    /// <summary>
    /// Binds a value the compiler itself needs — the tenant predicate, a
    /// cursor component — and returns its placeholder.
    /// </summary>
    /// <param name="name">The framework-chosen parameter name.</param>
    /// <param name="value">The value.</param>
    /// <returns>The parameter placeholder for this dialect.</returns>
    public string BindNamed(string name, object? value)
    {
        Guard.NotNullOrWhiteSpace(name, nameof(name));
        _parameters[name] = value;
        return _dialect.Parameter(name);
    }

    /// <summary>
    /// Compiles a filter tree.
    /// </summary>
    /// <param name="node">The tree root.</param>
    /// <returns>
    /// The predicate text, or failure — <see cref="ErrorCodes.InvalidFilter"/>
    /// for an unknown or non-filterable field.
    /// </returns>
    public Result<string> Compile(IFilterNode node)
    {
        Guard.NotNull(node, nameof(node));
        return node.Accept(this);
    }

    /// <inheritdoc />
    public Result<string> VisitComparison(ComparisonNode node)
    {
        Guard.NotNull(node, nameof(node));

        Result<IFieldMetadata> resolved = _metadata.ResolveField(node.FieldName);
        if (resolved.IsFailure)
        {
            return Result.Failure<string>(resolved.Error!);
        }

        IFieldMetadata field = resolved.Value;
        if (!field.IsFilterable)
        {
            // Names the field the caller supplied, and nothing else: listing
            // the filterable alternatives would make this a schema oracle.
            return Result.Failure<string>(new Error(
                ErrorCodes.InvalidFilter,
                $"Field '{node.FieldName}' is not filterable.",
                ErrorCategory.Validation));
        }

        string column = _dialect.QuoteIdentifier(field.ColumnName);

        return node.Operator switch
        {
            FilterOperator.Equal => Ok($"{column} = {Bind(node.Values[0])}"),
            FilterOperator.NotEqual => Ok($"{column} <> {Bind(node.Values[0])}"),
            FilterOperator.GreaterThan => Ok($"{column} > {Bind(node.Values[0])}"),
            FilterOperator.GreaterThanOrEqual => Ok($"{column} >= {Bind(node.Values[0])}"),
            FilterOperator.LessThan => Ok($"{column} < {Bind(node.Values[0])}"),
            FilterOperator.LessThanOrEqual => Ok($"{column} <= {Bind(node.Values[0])}"),
            FilterOperator.StartsWith => Ok(Like(column, EscapeLike(node.Values[0]) + "%")),
            FilterOperator.EndsWith => Ok(Like(column, "%" + EscapeLike(node.Values[0]))),
            FilterOperator.Contains => Ok(Like(column, "%" + EscapeLike(node.Values[0]) + "%")),
            FilterOperator.In => Ok($"{column} IN ({BindMany(node.Values)})"),
            FilterOperator.NotIn => Ok($"{column} NOT IN ({BindMany(node.Values)})"),
            FilterOperator.Between => Ok($"{column} BETWEEN {Bind(node.Values[0])} AND {Bind(node.Values[1])}"),
            FilterOperator.IsNull => Ok($"{column} IS NULL"),
            FilterOperator.IsNotNull => Ok($"{column} IS NOT NULL"),
            _ => Result.Failure<string>(new Error(
                ErrorCodes.InvalidFilter, "Unsupported filter operator.", ErrorCategory.Validation)),
        };
    }

    /// <inheritdoc />
    public Result<string> VisitCombination(CombinationNode node)
    {
        Guard.NotNull(node, nameof(node));

        string separator = node.Logic == FilterLogic.And ? " AND " : " OR ";
        var builder = new StringBuilder("(");

        for (int i = 0; i < node.Children.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(separator);
            }

            Result<string> child = node.Children[i].Accept(this);
            if (child.IsFailure)
            {
                return child;
            }

            builder.Append(child.Value);
        }

        return Ok(builder.Append(')').ToString());
    }

    private static Result<string> Ok(string sql) => Result.Success(sql);

    private string Like(string column, string pattern)
        => $"{column} LIKE {Bind(pattern)} ESCAPE '\\'";

    /// <summary>
    /// Binds a value to a fresh framework-named parameter and returns its
    /// placeholder. The value never touches the SQL text.
    /// </summary>
    private string Bind(object? value)
    {
        string name = "p" + _parameterIndex.ToString(CultureInfo.InvariantCulture);
        _parameterIndex++;
        _parameters[name] = value;
        return _dialect.Parameter(name);
    }

    private string BindMany(IReadOnlyList<object?> values)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(Bind(values[i]));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Escapes LIKE wildcards in a caller value so a search for "50%" cannot
    /// silently become a match-everything pattern. A correctness fix, not an
    /// injection defence — the value is parameterised either way.
    /// </summary>
    private static string EscapeLike(object? value)
    {
        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

        // Character-wise rather than String.Replace: the StringComparison
        // overload does not exist on Tier 3 TFMs (ADR-002), and #if is
        // confined to Edpf.Compatibility.
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is '\\' or '%' or '_' or '[')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
