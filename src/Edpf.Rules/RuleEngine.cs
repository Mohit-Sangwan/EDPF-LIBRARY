using System;
using System.Collections.Generic;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;
using Edpf.Formula;

namespace Edpf.Rules;

/// <summary>
/// Evaluates decision tables (Phase 17c).
/// </summary>
/// <remarks>
/// <para>
/// Every condition and outcome is a Phase 08c formula, so the sandbox, the
/// decimal arithmetic and the classification propagation come along for free —
/// and there is exactly one expression evaluator in the codebase, which is
/// what ADR-026 requires.
/// </para>
/// <para>
/// The engine's obligations beyond producing a value: **say which rows
/// matched**, and **refuse ambiguity rather than resolving it silently.**
/// </para>
/// </remarks>
public sealed class RuleEngine
{
    private readonly FormulaEngine _formulas;
    private readonly List<DecisionTable> _tables = [];

    /// <summary>Initializes an engine.</summary>
    /// <param name="formulas">The expression engine; a default one when omitted.</param>
    public RuleEngine(FormulaEngine? formulas = null) => _formulas = formulas ?? new FormulaEngine();

    /// <summary>
    /// Registers a table after checking that every formula in it parses.
    /// </summary>
    /// <param name="table">The table.</param>
    /// <returns>Success, or a failure naming the row that does not parse.</returns>
    /// <remarks>
    /// Validated at registration, not discovered mid-adjudication. A condition
    /// that fails to parse when a claim run reaches it has already stopped the
    /// claim run.
    /// </remarks>
    public Result Register(DecisionTable table)
    {
        Guard.NotNull(table, nameof(table));

        foreach (DecisionRow row in table.Rows)
        {
            Result<FormulaNode> condition = _formulas.Parse(row.Condition);
            if (condition.IsFailure)
            {
                return Result.Failure(new Error(
                    ErrorCodes.ValidationFailed,
                    $"Row '{row.Name}' of table '{table.Name}' has an unparseable condition: "
                    + condition.Error!.Message,
                    ErrorCategory.Validation));
            }

            Result<FormulaNode> outcome = _formulas.Parse(row.Outcome);
            if (outcome.IsFailure)
            {
                return Result.Failure(new Error(
                    ErrorCodes.ValidationFailed,
                    $"Row '{row.Name}' of table '{table.Name}' has an unparseable outcome: "
                    + outcome.Error!.Message,
                    ErrorCategory.Validation));
            }
        }

        if (table.Fallback is not null)
        {
            Result<FormulaNode> fallback = _formulas.Parse(table.Fallback);
            if (fallback.IsFailure)
            {
                return Result.Failure(new Error(
                    ErrorCodes.ValidationFailed,
                    $"Table '{table.Name}' has an unparseable fallback: {fallback.Error!.Message}",
                    ErrorCategory.Validation));
            }
        }

        foreach (DecisionTable existing in _tables)
        {
            if (!string.Equals(existing.Name, table.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DateTimeOffset existingEnd = existing.EffectiveTo ?? DateTimeOffset.MaxValue;
            DateTimeOffset newEnd = table.EffectiveTo ?? DateTimeOffset.MaxValue;

            if (existing.EffectiveFrom < newEnd && table.EffectiveFrom < existingEnd)
            {
                return Result.Failure(new Error(
                    ErrorCodes.Duplicate,
                    $"'{table.Name}' is already defined over an overlapping effective period. Close the "
                    + "earlier version before opening a new one.",
                    ErrorCategory.Conflict));
            }
        }

        _tables.Add(table);
        return Result.Success();
    }

    /// <summary>
    /// Finds the version of a table in effect at a point in time.
    /// </summary>
    /// <param name="name">The table name.</param>
    /// <param name="asOf">The instant.</param>
    /// <returns>The table, or a failure.</returns>
    public Result<DecisionTable> Resolve(string name, DateTimeOffset asOf)
    {
        foreach (DecisionTable table in _tables)
        {
            if (string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase)
                && table.AppliesAt(asOf))
            {
                return Result.Success(table);
            }
        }

        return Result.Failure<DecisionTable>(new Error(
            ErrorCodes.NotFound, $"No decision table named '{name}' is in effect.", ErrorCategory.NotFound));
    }

    /// <summary>
    /// Evaluates a table against a set of inputs.
    /// </summary>
    /// <param name="table">The table.</param>
    /// <param name="context">The field value source.</param>
    /// <returns>The outcome, naming the rows that matched, or a failure.</returns>
    public Result<RuleOutcome> Evaluate(DecisionTable table, IFormulaContext context)
    {
        Guard.NotNull(table, nameof(table));
        Guard.NotNull(context, nameof(context));

        var matched = new List<DecisionRow>();

        foreach (DecisionRow row in table.Rows)
        {
            Result<FormulaValue> condition = _formulas.Evaluate(row.Condition, context);
            if (condition.IsFailure)
            {
                return Result.Failure<RuleOutcome>(new Error(
                    condition.Error!.Code,
                    $"Row '{row.Name}' of table '{table.Name}': {condition.Error.Message}",
                    condition.Error.Category));
            }

            if (condition.Value.Kind != FormulaValueKind.Boolean)
            {
                return Result.Failure<RuleOutcome>(new Error(
                    ErrorCodes.ValidationFailed,
                    $"Row '{row.Name}' of table '{table.Name}' has a condition that is not a yes-or-no "
                    + "question, so whether it matches is undefined.",
                    ErrorCategory.Validation));
            }

            if (condition.Value.Boolean)
            {
                matched.Add(row);

                // First stops as soon as it has an answer; every other policy
                // needs the full match set to apply its rule.
                if (table.HitPolicy == HitPolicy.First)
                {
                    break;
                }
            }
        }

        return matched.Count == 0
            ? NoMatch(table, context)
            : ApplyPolicy(table, matched, context);
    }

    private Result<RuleOutcome> NoMatch(DecisionTable table, IFormulaContext context)
    {
        if (table.Fallback is null)
        {
            // Reported, not silently empty. A caller reading an absent result
            // will interpret it, and the usual interpretation is zero — free
            // in a pricing table, none in a dosage table.
            return Result.Failure<RuleOutcome>(new Error(
                ErrorCodes.NotFound,
                $"No row of table '{table.Name}' matched, and the table declares no fallback. The input "
                + "falls in a gap the table does not cover.",
                ErrorCategory.NotFound));
        }

        Result<FormulaValue> value = _formulas.Evaluate(table.Fallback, context);
        return value.IsFailure
            ? Result.Failure<RuleOutcome>(value.Error!)
            : Result.Success(new RuleOutcome(value.Value, [], usedFallback: true));
    }

    private Result<RuleOutcome> ApplyPolicy(
        DecisionTable table, List<DecisionRow> matched, IFormulaContext context)
    {
        switch (table.HitPolicy)
        {
            case HitPolicy.First:
                return Single(table, matched[0], matched, context);

            case HitPolicy.Unique:
                if (matched.Count > 1)
                {
                    return Ambiguous(table, matched,
                        "the table declares a Unique hit policy, so overlapping rows are an authoring "
                        + "error rather than something to tiebreak");
                }

                return Single(table, matched[0], matched, context);

            case HitPolicy.Priority:
            {
                DecisionRow best = matched[0];
                int ties = 1;

                foreach (DecisionRow row in matched)
                {
                    if (ReferenceEquals(row, best))
                    {
                        continue;
                    }

                    if (row.Priority > best.Priority)
                    {
                        best = row;
                        ties = 1;
                    }
                    else if (row.Priority == best.Priority)
                    {
                        ties++;
                    }
                }

                // Two rows at the same priority is the same ambiguity Unique
                // rejects, wearing a different hat.
                return ties > 1
                    ? Ambiguous(table, matched,
                        $"two or more matching rows share priority {best.Priority}, so which one wins "
                        + "would depend on declaration order")
                    : Single(table, best, matched, context);
            }

            default:
                return Collect(table, matched, context);
        }
    }

    private Result<RuleOutcome> Single(
        DecisionTable table, DecisionRow row, List<DecisionRow> matched, IFormulaContext context)
    {
        Result<FormulaValue> value = _formulas.Evaluate(row.Outcome, context);
        if (value.IsFailure)
        {
            return Result.Failure<RuleOutcome>(new Error(
                value.Error!.Code,
                $"Row '{row.Name}' of table '{table.Name}': {value.Error.Message}",
                value.Error.Category));
        }

        return Result.Success(new RuleOutcome(value.Value, Names(matched), usedFallback: false));
    }

    private Result<RuleOutcome> Collect(
        DecisionTable table, List<DecisionRow> matched, IFormulaContext context)
    {
        var values = new List<FormulaValue>(matched.Count);

        foreach (DecisionRow row in matched)
        {
            Result<FormulaValue> value = _formulas.Evaluate(row.Outcome, context);
            if (value.IsFailure)
            {
                return Result.Failure<RuleOutcome>(new Error(
                    value.Error!.Code,
                    $"Row '{row.Name}' of table '{table.Name}': {value.Error.Message}",
                    value.Error.Category));
            }

            values.Add(value.Value);
        }

        return Result.Success(new RuleOutcome(values[0], Names(matched), usedFallback: false)
        {
            CollectedValues = values,
        });
    }

    private static Result<RuleOutcome> Ambiguous(
        DecisionTable table, List<DecisionRow> matched, string why)
        => Result.Failure<RuleOutcome>(new Error(
            ErrorCodes.ValidationFailed,
            $"Rows {string.Join(", ", Names(matched))} of table '{table.Name}' all matched, and {why}.",
            ErrorCategory.Validation));

    private static List<string> Names(List<DecisionRow> rows)
    {
        var names = new List<string>(rows.Count);
        foreach (DecisionRow row in rows)
        {
            names.Add(row.Name);
        }

        return names;
    }
}
