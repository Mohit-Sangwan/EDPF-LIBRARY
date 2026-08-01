using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Edpf.Abstractions.Data;

namespace Edpf.Data.Dialects;

/// <summary>
/// Shared dialect behaviour, including the identifier validation every engine
/// needs. Concrete dialects override only what genuinely differs.
/// </summary>
public abstract class SqlDialectBase : ISqlDialect
{
    /// <inheritdoc />
    public abstract string ProviderName { get; }

    /// <inheritdoc />
    public virtual string ParameterPrefix => "@";

    /// <summary>The character opening a quoted identifier.</summary>
    protected abstract char QuoteOpen { get; }

    /// <summary>The character closing a quoted identifier.</summary>
    protected abstract char QuoteClose { get; }

    /// <summary>Maximum identifier length this engine accepts.</summary>
    protected abstract int MaxIdentifierLength { get; }

    /// <summary>
    /// Quotes an identifier, **rejecting** anything that is not a legal
    /// identifier rather than escaping it.
    /// </summary>
    /// <param name="identifier">A metadata-resolved identifier.</param>
    /// <returns>The quoted identifier.</returns>
    /// <exception cref="ArgumentException">
    /// The identifier is blank, over-long, or contains a character no legal
    /// identifier may contain. Escaping would accept hostile input and try to
    /// neutralise it; rejecting refuses it. An identifier that needs escaping
    /// did not come from metadata, which means something upstream is wrong.
    /// </exception>
    public string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Identifier must not be blank.", nameof(identifier));
        }

        if (identifier.Length > MaxIdentifierLength)
        {
            throw new ArgumentException(
                $"Identifier exceeds the {ProviderName} limit of {MaxIdentifierLength} characters.",
                nameof(identifier));
        }

        foreach (char c in identifier)
        {
            bool legal = char.IsLetterOrDigit(c) || c == '_' || c == '.';
            if (!legal)
            {
                throw new ArgumentException(
                    "Identifier contains an illegal character. Identifiers come from entity metadata; "
                    + "a value that needs escaping did not.",
                    nameof(identifier));
            }
        }

        // Dotted names are schema-qualified: quote each part separately so a
        // dot cannot smuggle structure past the quoting.
        string[] parts = identifier.Split('.');
        return string.Join(".", parts.Select(part => QuoteOpen + part + QuoteClose));
    }

    /// <inheritdoc />
    public virtual string Parameter(string parameterName)
    {
        if (string.IsNullOrWhiteSpace(parameterName))
        {
            throw new ArgumentException("Parameter name must not be blank.", nameof(parameterName));
        }

        foreach (char c in parameterName)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                throw new ArgumentException(
                    "Parameter names are framework-generated and must be alphanumeric.",
                    nameof(parameterName));
            }
        }

        return ParameterPrefix + parameterName;
    }

    /// <inheritdoc />
    public abstract string PaginationClause(string skipParameter, string takeParameter);

    /// <summary>
    /// Renders the keyset predicate as lexicographic row comparison expanded
    /// into OR-of-AND form, which every engine optimises and which stays
    /// correct for mixed sort directions.
    /// </summary>
    /// <param name="orderedColumns">The ordering columns, outermost first.</param>
    /// <param name="cursorParameters">Cursor parameter names, positionally matched.</param>
    /// <returns>A predicate selecting strictly the rows after the cursor.</returns>
    public virtual string KeysetPredicate(
        IReadOnlyList<SortColumn> orderedColumns,
        IReadOnlyList<string> cursorParameters)
    {
        if (orderedColumns is null)
        {
            throw new ArgumentNullException(nameof(orderedColumns));
        }

        if (cursorParameters is null)
        {
            throw new ArgumentNullException(nameof(cursorParameters));
        }

        if (orderedColumns.Count == 0)
        {
            throw new ArgumentException("Keyset pagination requires at least one sort column.", nameof(orderedColumns));
        }

        if (orderedColumns.Count != cursorParameters.Count)
        {
            throw new ArgumentException(
                "Cursor parameters must match the sort columns positionally.", nameof(cursorParameters));
        }

        // (a > @a) OR (a = @a AND b > @b) OR (a = @a AND b = @b AND c > @c)
        var alternatives = new List<string>(orderedColumns.Count);
        for (int i = 0; i < orderedColumns.Count; i++)
        {
            var terms = new List<string>(i + 1);
            for (int j = 0; j < i; j++)
            {
                terms.Add($"{QuoteIdentifier(orderedColumns[j].ColumnName)} = {Parameter(cursorParameters[j])}");
            }

            string comparison = orderedColumns[i].Descending ? "<" : ">";
            terms.Add(
                $"{QuoteIdentifier(orderedColumns[i].ColumnName)} {comparison} {Parameter(cursorParameters[i])}");

            alternatives.Add("(" + string.Join(" AND ", terms) + ")");
        }

        return "(" + string.Join(" OR ", alternatives) + ")";
    }

    /// <inheritdoc />
    public abstract string IdentityRetrievalClause();

    /// <inheritdoc />
    public abstract string UtcNowExpression();

    /// <inheritdoc />
    public virtual string Concat(IReadOnlyList<string> expressions)
    {
        if (expressions is null)
        {
            throw new ArgumentNullException(nameof(expressions));
        }

        if (expressions.Count == 0)
        {
            throw new ArgumentException("Concat requires at least one expression.", nameof(expressions));
        }

        return string.Join(" || ", expressions);
    }

    /// <inheritdoc />
    public virtual string BooleanLiteral(bool value)
        => value.ToString(CultureInfo.InvariantCulture).ToUpperInvariant();

    /// <inheritdoc />
    public abstract string JsonValue(string columnExpression, string jsonPathParameter);

    /// <summary>Renders an ORDER BY clause from validated sort columns.</summary>
    /// <param name="sort">The sort columns, outermost first.</param>
    /// <returns>The clause, without the leading <c>ORDER BY</c> keyword.</returns>
    public string OrderByList(IReadOnlyList<SortColumn> sort)
    {
        if (sort is null)
        {
            throw new ArgumentNullException(nameof(sort));
        }

        var builder = new StringBuilder();
        for (int i = 0; i < sort.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(QuoteIdentifier(sort[i].ColumnName));
            builder.Append(sort[i].Descending ? " DESC" : " ASC");
        }

        return builder.ToString();
    }
}
