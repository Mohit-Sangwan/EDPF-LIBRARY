using System;
using System.Collections.Generic;
using Edpf.Abstractions.Primitives;

namespace Edpf.Formula;

/// <summary>
/// The set of functions a formula may call (Phase 08c).
/// </summary>
/// <remarks>
/// A registry rather than an open dispatch: a name that is not registered
/// fails at parse time. There is no path by which an author-supplied string
/// reaches a lookup that might resolve to something unintended.
/// </remarks>
public interface IFormulaFunctionRegistry
{
    /// <summary>
    /// Whether <paramref name="name"/> is a registered function.
    /// </summary>
    /// <param name="name">The function name, case-insensitive.</param>
    /// <returns>Whether it is registered.</returns>
    bool Contains(string name);

    /// <summary>
    /// Checks the argument count for <paramref name="name"/>.
    /// </summary>
    /// <param name="name">The function name.</param>
    /// <param name="argumentCount">The number of arguments supplied.</param>
    /// <returns>The count, or a failure explaining the expected arity.</returns>
    Result<int> ValidateArity(string name, int argumentCount);
}

/// <summary>
/// The standard function library (Phase 08c).
/// </summary>
/// <remarks>
/// <para>
/// Math, string, date, logical, aggregate and statistical, per the phase.
/// **Nothing here performs I/O, reflection, code generation, or any operation
/// whose cost is not a function of its arguments.** That is not a coding
/// convention; it is what makes the sandbox a sandbox, and an architecture
/// test enforces it.
/// </para>
/// <para>
/// Absent by design: anything resembling <c>INDIRECT</c>, <c>EVAL</c>,
/// <c>WEBSERVICE</c> or <c>REPT</c>. The first two turn data into code, the
/// third performs I/O, and the fourth is a memory amplifier with no legitimate
/// use in a billing or dosage calculation.
/// </para>
/// </remarks>
public sealed class FormulaFunctions : IFormulaFunctionRegistry
{
    private readonly Dictionary<string, (int Min, int Max)> _arities;

    private FormulaFunctions(Dictionary<string, (int Min, int Max)> arities) => _arities = arities;

    /// <summary>The standard library.</summary>
    public static FormulaFunctions Standard { get; } = new(new Dictionary<string, (int, int)>(
        StringComparer.OrdinalIgnoreCase)
    {
        // Math
        ["ABS"] = (1, 1),
        ["ROUND"] = (2, 2),
        ["ROUNDDOWN"] = (2, 2),
        ["ROUNDUP"] = (2, 2),
        ["CEILING"] = (1, 1),
        ["FLOOR"] = (1, 1),
        ["POWER"] = (2, 2),
        ["SQRT"] = (1, 1),
        ["MOD"] = (2, 2),
        ["SIGN"] = (1, 1),

        // Aggregate and statistical. Variadic, capped by the node ceiling.
        ["SUM"] = (1, int.MaxValue),
        ["MIN"] = (1, int.MaxValue),
        ["MAX"] = (1, int.MaxValue),
        ["AVERAGE"] = (1, int.MaxValue),
        ["COUNT"] = (1, int.MaxValue),
        ["MEDIAN"] = (1, int.MaxValue),

        // Logical
        ["IF"] = (3, 3),
        ["AND"] = (1, int.MaxValue),
        ["OR"] = (1, int.MaxValue),
        ["NOT"] = (1, 1),
        ["ISBLANK"] = (1, 1),
        ["COALESCE"] = (1, int.MaxValue),

        // String
        ["CONCAT"] = (1, int.MaxValue),
        ["LEN"] = (1, 1),
        ["UPPER"] = (1, 1),
        ["LOWER"] = (1, 1),
        ["TRIM"] = (1, 1),
        ["LEFT"] = (2, 2),
        ["RIGHT"] = (2, 2),
        ["MID"] = (3, 3),
        ["CONTAINS"] = (2, 2),

        // Date
        ["YEAR"] = (1, 1),
        ["MONTH"] = (1, 1),
        ["DAY"] = (1, 1),
        ["DAYSBETWEEN"] = (2, 2),
        ["YEARSBETWEEN"] = (2, 2),
    });

    /// <inheritdoc />
    public bool Contains(string name) => name is not null && _arities.ContainsKey(name);

    /// <inheritdoc />
    public Result<int> ValidateArity(string name, int argumentCount)
    {
        if (name is null || !_arities.TryGetValue(name, out (int Min, int Max) arity))
        {
            return Result.Failure<int>(new Error(
                ErrorCodes.ValidationFailed, $"'{name}' is not a known function.", ErrorCategory.Validation));
        }

        if (argumentCount < arity.Min || argumentCount > arity.Max)
        {
            string expected = arity.Max == int.MaxValue
                ? $"at least {arity.Min}"
                : arity.Min == arity.Max
                    ? $"exactly {arity.Min}"
                    : $"between {arity.Min} and {arity.Max}";

            return Result.Failure<int>(new Error(
                ErrorCodes.ValidationFailed,
                $"'{name}' takes {expected} argument(s); {argumentCount} were supplied.",
                ErrorCategory.Validation));
        }

        return Result.Success(argumentCount);
    }

    /// <summary>Every registered function name.</summary>
    public IReadOnlyCollection<string> Names => _arities.Keys;
}
