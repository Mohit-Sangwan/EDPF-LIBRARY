using System;
using System.Collections.Generic;
using Edpf.Core.Guards;

namespace Edpf.Data.Query;

/// <summary>
/// A compiled statement and its parameters. The only thing a caller ever
/// supplies is a parameter **value**; every byte of the SQL text is emitted by
/// the framework from metadata-resolved identifiers and a closed operator set
/// (ADR-018).
/// </summary>
public sealed class CompiledQuery
{
    /// <summary>
    /// Initializes a compiled query.
    /// </summary>
    /// <param name="sql">The statement text.</param>
    /// <param name="parameters">Parameter name → value.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public CompiledQuery(string sql, IReadOnlyDictionary<string, object?> parameters)
    {
        Sql = Guard.NotNullOrWhiteSpace(sql, nameof(sql));
        Parameters = Guard.NotNull(parameters, nameof(parameters));
    }

    /// <summary>The statement text. Contains no caller-supplied characters.</summary>
    public string Sql { get; }

    /// <summary>Parameter name → value. Every caller value lives here.</summary>
    public IReadOnlyDictionary<string, object?> Parameters { get; }

    /// <summary>
    /// The statement with parameter names left intact — safe to log and to
    /// use as a plan-cache key, because it carries no values.
    /// </summary>
    public override string ToString() => Sql;
}
