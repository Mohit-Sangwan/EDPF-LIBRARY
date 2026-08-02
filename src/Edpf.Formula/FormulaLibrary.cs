using System;
using System.Collections.Generic;
using System.Globalization;
using Edpf.Abstractions.Primitives;

namespace Edpf.Formula;

/// <summary>
/// The standard functions' implementations (Phase 08c).
/// </summary>
/// <remarks>
/// Every function here is a pure transformation of its arguments. **No I/O, no
/// reflection, no code generation, no ambient state, no clock** — a formula
/// evaluated twice with the same inputs gives the same answer, which is what
/// makes a formula unit-testable before it goes live.
/// </remarks>
internal static class FormulaLibrary
{
    public static Result<FormulaValue> Invoke(
        string name, IReadOnlyList<FormulaValue> args, FormulaLimits limits)
    {
        switch (name.ToUpperInvariant())
        {
            case "ABS": return Unary(args, Math.Abs);
            case "SIGN": return Unary(args, v => Math.Sign(v));
            case "SQRT": return Sqrt(args);
            case "CEILING": return Unary(args, Math.Ceiling);
            case "FLOOR": return Unary(args, Math.Floor);
            case "ROUND": return Round(args, MidpointRounding.ToEven);
            case "ROUNDDOWN": return RoundDirected(args, up: false);
            case "ROUNDUP": return RoundDirected(args, up: true);
            case "MOD": return Mod(args);
            case "POWER": return Power(args);

            case "SUM": return Aggregate(args, static values => Sum(values));
            case "MIN": return Aggregate(args, static values => Extreme(values, min: true));
            case "MAX": return Aggregate(args, static values => Extreme(values, min: false));
            case "AVERAGE": return Average(args);
            case "MEDIAN": return Median(args);
            case "COUNT": return Count(args);

            case "AND": return Logical(args, all: true);
            case "OR": return Logical(args, all: false);
            case "NOT": return Not(args);
            case "ISBLANK": return Result.Success(
                FormulaValue.FromBoolean(args[0].Kind == FormulaValueKind.Blank));
            case "COALESCE": return Coalesce(args);

            case "CONCAT": return Concat(args, limits);
            case "LEN": return Result.Success(
                FormulaValue.FromNumber(FormulaEvaluator.ToText(args[0]).Length));
            case "UPPER": return Text(args, static s => s.ToUpperInvariant());
            case "LOWER": return Text(args, static s => s.ToLowerInvariant());
            case "TRIM": return Text(args, static s => s.Trim());
            case "LEFT": return Substring(args, fromLeft: true);
            case "RIGHT": return Substring(args, fromLeft: false);
            case "MID": return Mid(args);
            case "CONTAINS": return Contains(args);

            case "YEAR": return DatePart(args, static d => d.Year);
            case "MONTH": return DatePart(args, static d => d.Month);
            case "DAY": return DatePart(args, static d => d.Day);
            case "DAYSBETWEEN": return DaysBetween(args);
            case "YEARSBETWEEN": return YearsBetween(args);

            default:
                return FormulaEvaluator.Failure($"'{name}' is not a known function.");
        }
    }

    private static Result<FormulaValue> Unary(IReadOnlyList<FormulaValue> args, Func<decimal, decimal> op)
        => args[0].Kind != FormulaValueKind.Number
            ? FormulaEvaluator.Failure("A number is required.")
            : Result.Success(FormulaValue.FromNumber(op(args[0].Number)));

    private static Result<FormulaValue> Sqrt(IReadOnlyList<FormulaValue> args)
    {
        if (args[0].Kind != FormulaValueKind.Number)
        {
            return FormulaEvaluator.Failure("SQRT requires a number.");
        }

        decimal value = args[0].Number;
        if (value < 0m)
        {
            return FormulaEvaluator.Failure("SQRT requires a non-negative number.");
        }

        if (value == 0m)
        {
            return Result.Success(FormulaValue.FromNumber(0m));
        }

        // Newton-Raphson in decimal. Math.Sqrt would route through double and
        // reintroduce exactly the binary rounding the decimal requirement
        // exists to keep out of a dosage or an invoice.
        decimal guess = value / 2m;
        for (int i = 0; i < 64; i++)
        {
            decimal next = (guess + (value / guess)) / 2m;
            if (next == guess)
            {
                break;
            }

            guess = next;
        }

        return Result.Success(FormulaValue.FromNumber(guess));
    }

    private static Result<FormulaValue> Round(IReadOnlyList<FormulaValue> args, MidpointRounding mode)
    {
        if (args[0].Kind != FormulaValueKind.Number || args[1].Kind != FormulaValueKind.Number)
        {
            return FormulaEvaluator.Failure("ROUND requires two numbers.");
        }

        int digits = (int)args[1].Number;
        if (digits < 0 || digits > 28)
        {
            return FormulaEvaluator.Failure("ROUND supports 0 to 28 decimal places.");
        }

        return Result.Success(FormulaValue.FromNumber(Math.Round(args[0].Number, digits, mode)));
    }

    private static Result<FormulaValue> RoundDirected(IReadOnlyList<FormulaValue> args, bool up)
    {
        if (args[0].Kind != FormulaValueKind.Number || args[1].Kind != FormulaValueKind.Number)
        {
            return FormulaEvaluator.Failure("Rounding requires two numbers.");
        }

        int digits = (int)args[1].Number;
        if (digits < 0 || digits > 28)
        {
            return FormulaEvaluator.Failure("Rounding supports 0 to 28 decimal places.");
        }

        decimal scale = 1m;
        for (int i = 0; i < digits; i++)
        {
            scale *= 10m;
        }

        decimal scaled = args[0].Number * scale;
        decimal rounded = up ? Math.Ceiling(scaled) : Math.Floor(scaled);
        return Result.Success(FormulaValue.FromNumber(rounded / scale));
    }

    private static Result<FormulaValue> Mod(IReadOnlyList<FormulaValue> args)
    {
        if (args[0].Kind != FormulaValueKind.Number || args[1].Kind != FormulaValueKind.Number)
        {
            return FormulaEvaluator.Failure("MOD requires two numbers.");
        }

        return args[1].Number == 0m
            ? FormulaEvaluator.Failure("Division by zero.")
            : Result.Success(FormulaValue.FromNumber(args[0].Number % args[1].Number));
    }

    private static Result<FormulaValue> Aggregate(
        IReadOnlyList<FormulaValue> args, Func<List<decimal>, decimal> reduce)
    {
        Result<List<decimal>> numbers = Numbers(args);
        return numbers.IsFailure
            ? Result.Failure<FormulaValue>(numbers.Error!)
            : numbers.Value.Count == 0
                ? Result.Success(FormulaValue.Blank)
                : Result.Success(FormulaValue.FromNumber(reduce(numbers.Value)));
    }

    private static decimal Sum(List<decimal> values)
    {
        decimal total = 0m;
        foreach (decimal value in values)
        {
            total += value;
        }

        return total;
    }

    private static decimal Extreme(List<decimal> values, bool min)
    {
        decimal best = values[0];
        foreach (decimal value in values)
        {
            if (min ? value < best : value > best)
            {
                best = value;
            }
        }

        return best;
    }

    private static Result<FormulaValue> Average(IReadOnlyList<FormulaValue> args)
    {
        Result<List<decimal>> numbers = Numbers(args);
        if (numbers.IsFailure)
        {
            return Result.Failure<FormulaValue>(numbers.Error!);
        }

        // Blank rather than zero: an average of nothing is not zero, and
        // returning zero would silently understate a KPI built on it.
        return numbers.Value.Count == 0
            ? Result.Success(FormulaValue.Blank)
            : Result.Success(FormulaValue.FromNumber(Sum(numbers.Value) / numbers.Value.Count));
    }

    private static Result<FormulaValue> Median(IReadOnlyList<FormulaValue> args)
    {
        Result<List<decimal>> numbers = Numbers(args);
        if (numbers.IsFailure)
        {
            return Result.Failure<FormulaValue>(numbers.Error!);
        }

        List<decimal> values = numbers.Value;
        if (values.Count == 0)
        {
            return Result.Success(FormulaValue.Blank);
        }

        values.Sort();
        int middle = values.Count / 2;

        decimal median = values.Count % 2 == 1
            ? values[middle]
            : (values[middle - 1] + values[middle]) / 2m;

        return Result.Success(FormulaValue.FromNumber(median));
    }

    private static Result<FormulaValue> Count(IReadOnlyList<FormulaValue> args)
    {
        int count = 0;
        foreach (FormulaValue value in args)
        {
            if (value.Kind != FormulaValueKind.Blank)
            {
                count++;
            }
        }

        return Result.Success(FormulaValue.FromNumber(count));
    }

    private static Result<List<decimal>> Numbers(IReadOnlyList<FormulaValue> args)
    {
        var numbers = new List<decimal>(args.Count);

        foreach (FormulaValue value in args)
        {
            // Blanks are skipped, not coerced to zero: an unrecorded weight is
            // not a weight of zero, and averaging it in would be a clinically
            // wrong answer arrived at silently.
            if (value.Kind == FormulaValueKind.Blank)
            {
                continue;
            }

            if (value.Kind != FormulaValueKind.Number)
            {
                return Result.Failure<List<decimal>>(new Error(
                    ErrorCodes.ValidationFailed,
                    "An aggregate requires numbers.",
                    ErrorCategory.Validation));
            }

            numbers.Add(value.Number);
        }

        return Result.Success(numbers);
    }

    private static Result<FormulaValue> Logical(IReadOnlyList<FormulaValue> args, bool all)
    {
        bool result = all;

        foreach (FormulaValue value in args)
        {
            if (value.Kind != FormulaValueKind.Boolean)
            {
                return FormulaEvaluator.Failure("A logical function requires booleans.");
            }

            result = all ? result && value.Boolean : result || value.Boolean;
        }

        return Result.Success(FormulaValue.FromBoolean(result));
    }

    private static Result<FormulaValue> Not(IReadOnlyList<FormulaValue> args)
        => args[0].Kind != FormulaValueKind.Boolean
            ? FormulaEvaluator.Failure("NOT requires a boolean.")
            : Result.Success(FormulaValue.FromBoolean(!args[0].Boolean));

    private static Result<FormulaValue> Coalesce(IReadOnlyList<FormulaValue> args)
    {
        foreach (FormulaValue value in args)
        {
            if (value.Kind != FormulaValueKind.Blank)
            {
                return Result.Success(value);
            }
        }

        return Result.Success(FormulaValue.Blank);
    }

    private static Result<FormulaValue> Concat(IReadOnlyList<FormulaValue> args, FormulaLimits limits)
    {
        var builder = new System.Text.StringBuilder();

        foreach (FormulaValue value in args)
        {
            builder.Append(FormulaEvaluator.ToText(value));

            if (builder.Length > limits.MaxTextLength)
            {
                return FormulaEvaluator.LimitFailure(
                    $"A text result exceeds the maximum length of {limits.MaxTextLength}.");
            }
        }

        return Result.Success(FormulaValue.FromText(builder.ToString()));
    }

    private static Result<FormulaValue> Text(IReadOnlyList<FormulaValue> args, Func<string, string> op)
        => Result.Success(FormulaValue.FromText(op(FormulaEvaluator.ToText(args[0]))));

    private static Result<FormulaValue> Substring(IReadOnlyList<FormulaValue> args, bool fromLeft)
    {
        if (args[1].Kind != FormulaValueKind.Number)
        {
            return FormulaEvaluator.Failure("A character count is required.");
        }

        string text = FormulaEvaluator.ToText(args[0]);
        int count = (int)args[1].Number;

        if (count < 0)
        {
            return FormulaEvaluator.Failure("A character count must not be negative.");
        }

        // Clamped rather than refused: asking for more characters than exist
        // is ordinary in a spreadsheet and returning the whole string is what
        // an author expects.
        count = Math.Min(count, text.Length);

        return Result.Success(FormulaValue.FromText(
            fromLeft ? text.Substring(0, count) : text.Substring(text.Length - count, count)));
    }

    private static Result<FormulaValue> Mid(IReadOnlyList<FormulaValue> args)
    {
        if (args[1].Kind != FormulaValueKind.Number || args[2].Kind != FormulaValueKind.Number)
        {
            return FormulaEvaluator.Failure("MID requires a start position and a length.");
        }

        string text = FormulaEvaluator.ToText(args[0]);

        // 1-based, as in every spreadsheet.
        int start = (int)args[1].Number - 1;
        int length = (int)args[2].Number;

        if (start < 0 || length < 0)
        {
            return FormulaEvaluator.Failure("MID requires a positive start position and a non-negative length.");
        }

        if (start >= text.Length)
        {
            return Result.Success(FormulaValue.FromText(string.Empty));
        }

        length = Math.Min(length, text.Length - start);
        return Result.Success(FormulaValue.FromText(text.Substring(start, length)));
    }

    private static Result<FormulaValue> Contains(IReadOnlyList<FormulaValue> args)
    {
        // Ordinal: a rule's verdict must not depend on the server's culture
        // (Phase 27). Under a Turkish culture, culture-aware matching makes
        // "I" and "ı" the same letter and the rule decides differently.
        string haystack = FormulaEvaluator.ToText(args[0]);
        string needle = FormulaEvaluator.ToText(args[1]);

        return Result.Success(FormulaValue.FromBoolean(
            haystack.Contains(needle, StringComparison.Ordinal)));
    }

    private static Result<FormulaValue> DatePart(IReadOnlyList<FormulaValue> args, Func<DateTimeOffset, int> part)
        => args[0].Kind != FormulaValueKind.Timestamp
            ? FormulaEvaluator.Failure("A date is required.")
            : Result.Success(FormulaValue.FromNumber(part(args[0].Timestamp)));

    private static Result<FormulaValue> DaysBetween(IReadOnlyList<FormulaValue> args)
    {
        if (args[0].Kind != FormulaValueKind.Timestamp || args[1].Kind != FormulaValueKind.Timestamp)
        {
            return FormulaEvaluator.Failure("DAYSBETWEEN requires two dates.");
        }

        return Result.Success(FormulaValue.FromNumber(
            (decimal)(args[1].Timestamp - args[0].Timestamp).TotalDays));
    }

    private static Result<FormulaValue> YearsBetween(IReadOnlyList<FormulaValue> args)
    {
        if (args[0].Kind != FormulaValueKind.Timestamp || args[1].Kind != FormulaValueKind.Timestamp)
        {
            return FormulaEvaluator.Failure("YEARSBETWEEN requires two dates.");
        }

        DateTimeOffset from = args[0].Timestamp;
        DateTimeOffset to = args[1].Timestamp;

        // Whole elapsed years, counted the way an age is: not days/365.25,
        // which drifts, and not a year difference, which says a person born on
        // 31 December is one year old the next day.
        int years = to.Year - from.Year;
        if (to < from.AddYears(years))
        {
            years--;
        }

        return Result.Success(FormulaValue.FromNumber(years));
    }

    private static Result<FormulaValue> Power(IReadOnlyList<FormulaValue> args)
    {
        // Delegates to the evaluator's implementation rather than repeating
        // it. Two copies of decimal exponentiation would eventually disagree,
        // and the operator form and the function form must be the same
        // calculation or `2^3` and `POWER(2,3)` could differ.
        if (args[0].Kind != FormulaValueKind.Number || args[1].Kind != FormulaValueKind.Number)
        {
            return FormulaEvaluator.Failure("POWER requires two numbers.");
        }

        return FormulaEvaluator.Power(args[0].Number, args[1].Number, DataClassificationLevel.Public);
    }
}
