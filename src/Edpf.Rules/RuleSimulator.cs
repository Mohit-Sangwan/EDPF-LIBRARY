using System;
using System.Collections.Generic;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;
using Edpf.Formula;

namespace Edpf.Rules;

/// <summary>
/// One simulated case and what the table did with it (Phase 17c).
/// </summary>
public sealed class SimulationCase
{
    /// <summary>Initializes a case.</summary>
    /// <param name="name">A label for the case.</param>
    /// <param name="context">The inputs.</param>
    public SimulationCase(string name, IFormulaContext context)
    {
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name));
        Context = Guard.NotNull(context, nameof(context));
    }

    /// <summary>A label for the case.</summary>
    public string Name { get; }

    /// <summary>The inputs.</summary>
    public IFormulaContext Context { get; }
}

/// <summary>The result of running one case (Phase 17c).</summary>
public sealed class SimulationResult
{
    /// <summary>Initializes a result.</summary>
    /// <param name="caseName">The case label.</param>
    /// <param name="outcome">The outcome, when the table produced one.</param>
    /// <param name="error">The error, when it did not.</param>
    public SimulationResult(string caseName, RuleOutcome? outcome, Error? error)
    {
        CaseName = Guard.NotNullOrWhiteSpace(caseName, nameof(caseName));
        Outcome = outcome;
        Error = error;
    }

    /// <summary>The case label.</summary>
    public string CaseName { get; }

    /// <summary>The outcome, when the table produced one.</summary>
    public RuleOutcome? Outcome { get; }

    /// <summary>The error, when it did not.</summary>
    public Error? Error { get; }

    /// <summary>Whether the table produced a result.</summary>
    public bool Succeeded => Outcome is not null;
}

/// <summary>
/// The findings of a table analysis (Phase 17c).
/// </summary>
public sealed class TableAnalysis
{
    /// <summary>Initializes an analysis.</summary>
    /// <param name="uncoveredCases">Cases no row matched.</param>
    /// <param name="overlappingCases">Cases more than one row matched, with the row names.</param>
    /// <param name="unreachableRows">Rows no case reached.</param>
    /// <param name="failedCases">Cases that produced an error.</param>
    public TableAnalysis(
        IReadOnlyList<string> uncoveredCases,
        IReadOnlyList<string> overlappingCases,
        IReadOnlyList<string> unreachableRows,
        IReadOnlyList<string> failedCases)
    {
        UncoveredCases = Guard.NotNull(uncoveredCases, nameof(uncoveredCases));
        OverlappingCases = Guard.NotNull(overlappingCases, nameof(overlappingCases));
        UnreachableRows = Guard.NotNull(unreachableRows, nameof(unreachableRows));
        FailedCases = Guard.NotNull(failedCases, nameof(failedCases));
    }

    /// <summary>Cases no row matched — the gaps in the table.</summary>
    public IReadOnlyList<string> UncoveredCases { get; }

    /// <summary>Cases more than one row matched.</summary>
    public IReadOnlyList<string> OverlappingCases { get; }

    /// <summary>
    /// Rows no case reached. Either the sample is too thin, or the row is dead
    /// — and a dead row is usually a condition that can never be true, which
    /// means the rule its author intended is not in force.
    /// </summary>
    public IReadOnlyList<string> UnreachableRows { get; }

    /// <summary>Cases that produced an error.</summary>
    public IReadOnlyList<string> FailedCases { get; }

    /// <summary>True when nothing was found.</summary>
    public bool IsClean
        => UncoveredCases.Count == 0
            && OverlappingCases.Count == 0
            && UnreachableRows.Count == 0
            && FailedCases.Count == 0;
}

/// <summary>
/// Runs a decision table against sample cases without committing anything
/// (Phase 17c — simulation and what-if).
/// </summary>
/// <remarks>
/// <para>
/// The phase requires that a rule be testable before it goes live. This is
/// what makes that true: an author supplies representative cases, and gets
/// back what the table would do — plus the three findings that matter more
/// than any individual answer.
/// </para>
/// <para>
/// **Gaps** — a case no row covers. With no fallback that is a runtime error
/// waiting for the input that triggers it.
/// </para>
/// <para>
/// **Overlaps** — a case several rows match. Harmless under
/// <see cref="HitPolicy.First"/>, an error under <see cref="HitPolicy.Unique"/>,
/// and under either it usually means the author did not realise two conditions
/// could both be true.
/// </para>
/// <para>
/// **Unreachable rows** — a row no case reached. Often just a thin sample, but
/// the alternative reading is a condition that can never be true, which means
/// a rule someone believes is in force is not.
/// </para>
/// <para>
/// Simulation is side-effect free by construction: evaluation reads through
/// <see cref="IFormulaContext"/> and writes nothing, and the formula engine
/// has no I/O to perform even if it wanted to (ADR-026).
/// </para>
/// </remarks>
public sealed class RuleSimulator
{
    private readonly RuleEngine _engine;

    /// <summary>Initializes a simulator.</summary>
    /// <param name="engine">The rule engine to simulate against.</param>
    public RuleSimulator(RuleEngine engine) => _engine = Guard.NotNull(engine, nameof(engine));

    /// <summary>
    /// Runs every case against the table.
    /// </summary>
    /// <param name="table">The table.</param>
    /// <param name="cases">The sample cases.</param>
    /// <returns>One result per case, in order.</returns>
    public IReadOnlyList<SimulationResult> Run(
        DecisionTable table, IReadOnlyList<SimulationCase> cases)
    {
        Guard.NotNull(table, nameof(table));
        Guard.NotNull(cases, nameof(cases));

        var results = new List<SimulationResult>(cases.Count);

        foreach (SimulationCase sample in cases)
        {
            Result<RuleOutcome> outcome = _engine.Evaluate(table, sample.Context);

            results.Add(outcome.IsSuccess
                ? new SimulationResult(sample.Name, outcome.Value, null)
                : new SimulationResult(sample.Name, null, outcome.Error));
        }

        return results;
    }

    /// <summary>
    /// Runs every case and reports gaps, overlaps and unreachable rows.
    /// </summary>
    /// <param name="table">The table.</param>
    /// <param name="cases">The sample cases.</param>
    /// <returns>The findings.</returns>
    /// <remarks>
    /// The analysis is only as good as the cases supplied. It cannot prove a
    /// table total — proving that would mean reasoning about the conditions
    /// symbolically, which the formula grammar permits but which is a separate
    /// piece of work. What it does is turn "we think this table is complete"
    /// into a claim checked against named examples.
    /// </remarks>
    public TableAnalysis Analyze(DecisionTable table, IReadOnlyList<SimulationCase> cases)
    {
        Guard.NotNull(table, nameof(table));
        Guard.NotNull(cases, nameof(cases));

        var uncovered = new List<string>();
        var overlapping = new List<string>();
        var failed = new List<string>();
        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (SimulationCase sample in cases)
        {
            Result<RuleOutcome> outcome = _engine.Evaluate(table, sample.Context);

            if (outcome.IsFailure)
            {
                // A no-match with no fallback is a gap, which is a distinct
                // finding from a formula that failed to evaluate. Collapsing
                // the two would hide whichever is rarer.
                if (outcome.Error!.Code == ErrorCodes.NotFound)
                {
                    uncovered.Add(sample.Name);
                }
                else
                {
                    failed.Add(sample.Name);
                }

                continue;
            }

            if (outcome.Value.UsedFallback)
            {
                uncovered.Add(sample.Name);
                continue;
            }

            if (outcome.Value.MatchedRows.Count > 1)
            {
                overlapping.Add($"{sample.Name} → {string.Join(", ", outcome.Value.MatchedRows)}");
            }

            foreach (string row in outcome.Value.MatchedRows)
            {
                reached.Add(row);
            }
        }

        // Under a First policy the engine stops at the first match, so rows
        // after it are never evaluated and would look unreachable. Reporting
        // them would be a false finding, and false findings are how a report
        // gets ignored.
        var unreachable = new List<string>();
        if (table.HitPolicy != HitPolicy.First)
        {
            foreach (DecisionRow row in table.Rows)
            {
                if (!reached.Contains(row.Name))
                {
                    unreachable.Add(row.Name);
                }
            }
        }

        return new TableAnalysis(uncovered, overlapping, unreachable, failed);
    }

    /// <summary>
    /// Compares two versions of a table over the same cases.
    /// </summary>
    /// <param name="before">The version in force.</param>
    /// <param name="after">The proposed version.</param>
    /// <param name="cases">The sample cases.</param>
    /// <returns>The case names whose outcome would change.</returns>
    /// <remarks>
    /// The what-if the phase asks for, and the question an author actually has
    /// before changing a live pricing or triage table: *which cases decide
    /// differently now?* A diff of the rule text does not answer that — two
    /// rewritten conditions can be equivalent, and one changed constant can
    /// move thousands of cases.
    /// </remarks>
    public IReadOnlyList<string> Compare(
        DecisionTable before, DecisionTable after, IReadOnlyList<SimulationCase> cases)
    {
        Guard.NotNull(before, nameof(before));
        Guard.NotNull(after, nameof(after));
        Guard.NotNull(cases, nameof(cases));

        var changed = new List<string>();

        foreach (SimulationCase sample in cases)
        {
            Result<RuleOutcome> first = _engine.Evaluate(before, sample.Context);
            Result<RuleOutcome> second = _engine.Evaluate(after, sample.Context);

            if (first.IsSuccess != second.IsSuccess)
            {
                changed.Add(sample.Name);
                continue;
            }

            if (first.IsSuccess && first.Value.Value != second.Value.Value)
            {
                changed.Add(sample.Name);
            }
        }

        return changed;
    }
}
