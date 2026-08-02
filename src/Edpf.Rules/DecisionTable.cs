using System;
using System.Collections.Generic;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;
using Edpf.Formula;

namespace Edpf.Rules;

/// <summary>
/// What a decision table does when more than one row matches (Phase 17c).
/// </summary>
/// <remarks>
/// <para>
/// **There is no default.** A table whose author has not said what happens
/// when two rows match is a table that will one day return whichever row
/// happened to be first — and in a pricing or triage table, that is a wrong
/// answer delivered confidently.
/// </para>
/// <para>
/// Making the policy a required, closed choice forces the question at
/// authoring time, which is the only time anyone is thinking about it.
/// </para>
/// </remarks>
public enum HitPolicy
{
    /// <summary>
    /// Exactly one row may match. Two matches is an error, not a tiebreak.
    /// The right default for tables that are meant to be exhaustive and
    /// mutually exclusive, because it detects the author's mistake.
    /// </summary>
    Unique = 0,

    /// <summary>The first matching row in declaration order wins.</summary>
    First = 1,

    /// <summary>
    /// The matching row with the highest priority wins. Ties are an error —
    /// two rows at the same priority is the same ambiguity as
    /// <see cref="Unique"/>.
    /// </summary>
    Priority = 2,

    /// <summary>Every matching row's outcome is collected.</summary>
    Collect = 3,
}

/// <summary>One row of a decision table (Phase 17c).</summary>
public sealed class DecisionRow
{
    /// <summary>Initializes a row.</summary>
    /// <param name="name">A name for the row, used in explanations and simulation output.</param>
    /// <param name="condition">The formula deciding whether this row matches; must yield a boolean.</param>
    /// <param name="outcome">The formula producing this row's result.</param>
    /// <param name="priority">Priority for <see cref="HitPolicy.Priority"/>; higher wins.</param>
    public DecisionRow(string name, string condition, string outcome, int priority = 0)
    {
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name));
        Condition = Guard.NotNullOrWhiteSpace(condition, nameof(condition));
        Outcome = Guard.NotNullOrWhiteSpace(outcome, nameof(outcome));
        Priority = priority;
    }

    /// <summary>A name for the row.</summary>
    public string Name { get; }

    /// <summary>The formula deciding whether this row matches.</summary>
    public string Condition { get; }

    /// <summary>The formula producing this row's result.</summary>
    public string Outcome { get; }

    /// <summary>Priority for <see cref="HitPolicy.Priority"/>; higher wins.</summary>
    public int Priority { get; }
}

/// <summary>
/// A decision table: rows of condition and outcome, with a declared policy for
/// what happens when several match (Phase 17c).
/// </summary>
/// <remarks>
/// <para>
/// Conditions and outcomes are formulas, evaluated by the Phase 08c engine.
/// There is deliberately no second expression language here — the sandbox,
/// the decimal arithmetic and the classification propagation are inherited
/// rather than reimplemented, and ADR-026 names a second evaluator as a
/// revisit trigger for exactly this reason.
/// </para>
/// <para>
/// Effective dating for the same reason formulas and metadata carry it: a
/// claim adjudicated in 2024 must be explainable from the rules that applied
/// in 2024, not from the ones in force when someone reopens the case.
/// </para>
/// </remarks>
public sealed class DecisionTable
{
    /// <summary>Initializes a decision table.</summary>
    /// <param name="name">The table name.</param>
    /// <param name="hitPolicy">What happens when more than one row matches.</param>
    /// <param name="rows">The rows, in declaration order.</param>
    /// <param name="effectiveFrom">When the table takes effect, inclusive.</param>
    /// <param name="effectiveTo">When it ceases to apply, exclusive; open-ended if null.</param>
    /// <param name="fallback">
    /// The outcome when no row matches. Omitted means "no match is an error" —
    /// see <see cref="Fallback"/> for why that is the safer default.
    /// </param>
    /// <exception cref="ArgumentException">There are no rows, or the effective range is inverted.</exception>
    public DecisionTable(
        string name,
        HitPolicy hitPolicy,
        IReadOnlyList<DecisionRow> rows,
        DateTimeOffset effectiveFrom,
        DateTimeOffset? effectiveTo = null,
        string? fallback = null)
    {
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name));
        HitPolicy = hitPolicy;
        Rows = Guard.NotNull(rows, nameof(rows));
        EffectiveFrom = effectiveFrom;
        EffectiveTo = effectiveTo;
        Fallback = fallback;

        if (rows.Count == 0)
        {
            throw new ArgumentException("A decision table must have at least one row.", nameof(rows));
        }

        if (effectiveTo.HasValue && effectiveTo.Value <= effectiveFrom)
        {
            throw new ArgumentException(
                "A table's effective-to must follow its effective-from.", nameof(effectiveTo));
        }
    }

    /// <summary>The table name.</summary>
    public string Name { get; }

    /// <summary>What happens when more than one row matches.</summary>
    public HitPolicy HitPolicy { get; }

    /// <summary>The rows, in declaration order.</summary>
    public IReadOnlyList<DecisionRow> Rows { get; }

    /// <summary>When the table takes effect, inclusive.</summary>
    public DateTimeOffset EffectiveFrom { get; }

    /// <summary>When it ceases to apply, exclusive.</summary>
    public DateTimeOffset? EffectiveTo { get; }

    /// <summary>
    /// The outcome when no row matches, or <see langword="null"/> when a
    /// non-match is an error.
    /// </summary>
    /// <remarks>
    /// Null is the safer default. A table with no fallback that meets an input
    /// it does not cover *says so*; a table that silently returns nothing
    /// leaves the caller to interpret an absence, and the usual interpretation
    /// is zero — which in a pricing table is free and in a dosage table is
    /// none.
    /// </remarks>
    public string? Fallback { get; }

    /// <summary>
    /// True when this table applies at <paramref name="asOf"/>.
    /// </summary>
    /// <param name="asOf">The instant to test.</param>
    /// <returns>Whether it is in effect.</returns>
    public bool AppliesAt(DateTimeOffset asOf)
        => asOf >= EffectiveFrom && (!EffectiveTo.HasValue || asOf < EffectiveTo.Value);
}

/// <summary>
/// What a decision table did, and why (Phase 17c).
/// </summary>
/// <remarks>
/// The explanation is not a debugging aid. A rules engine that produces an
/// answer nobody can account for is unusable in a regulated setting: someone
/// will be asked why this claim was denied, and "the table said so" is not an
/// answer that survives an audit or an appeal.
/// </remarks>
public sealed class RuleOutcome
{
    /// <summary>Initializes an outcome.</summary>
    /// <param name="value">The result.</param>
    /// <param name="matchedRows">The rows that matched, in evaluation order.</param>
    /// <param name="usedFallback">Whether the table's fallback produced the result.</param>
    public RuleOutcome(FormulaValue value, IReadOnlyList<string> matchedRows, bool usedFallback)
    {
        Value = value;
        MatchedRows = Guard.NotNull(matchedRows, nameof(matchedRows));
        UsedFallback = usedFallback;
    }

    /// <summary>The result.</summary>
    public FormulaValue Value { get; }

    /// <summary>The rows that matched, in evaluation order.</summary>
    public IReadOnlyList<string> MatchedRows { get; }

    /// <summary>Whether the fallback produced the result.</summary>
    public bool UsedFallback { get; }

    /// <summary>Every matching row's value, when the policy is <see cref="HitPolicy.Collect"/>.</summary>
    public IReadOnlyList<FormulaValue> CollectedValues { get; init; } = [];
}
