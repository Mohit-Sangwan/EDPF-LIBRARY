using Edpf.Formula;
using Edpf.Rules;
using Edpf.UnitTests.Formula;

namespace Edpf.UnitTests.Rules;

/// <summary>
/// Phase 17c — simulation and what-if. The phase requires that a rule be
/// testable before it goes live; this is what makes that true.
/// </summary>
public sealed class RuleSimulatorTests
{
    private static readonly DateTimeOffset Epoch = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly RuleSimulator Simulator = new(new RuleEngine());

    private static SimulationCase Case(string name, decimal score)
        => new(name, TestFormulaContext.WithValues(("Score", FormulaValue.FromNumber(score))));

    private static DecisionTable Tiering(HitPolicy policy = HitPolicy.Priority, string? fallback = null)
        => new(
            "Tiering",
            policy,
            [
                new DecisionRow("Low", "[Score] <= 30", "\"low\"", priority: 1),
                new DecisionRow("Medium", "[Score] > 30", "\"medium\"", priority: 2),
                new DecisionRow("High", "[Score] > 70", "\"high\"", priority: 3),
            ],
            Epoch,
            null,
            fallback);

    [Fact]
    public void Run_ReportsWhatEachCaseWouldProduce()
    {
        IReadOnlyList<SimulationResult> results = Simulator.Run(
            Tiering(),
            [Case("low", 10m), Case("mid", 50m), Case("high", 90m)]);

        Assert.Equal(3, results.Count);
        Assert.Equal("low", results[0].Outcome!.Value.Text);
        Assert.Equal("medium", results[1].Outcome!.Value.Text);
        Assert.Equal("high", results[2].Outcome!.Value.Text);
    }

    [Fact]
    public void Analyze_FindsAGapNoRowCovers()
    {
        // With no fallback, an uncovered input is a runtime error waiting for
        // the day someone enters it.
        var table = new DecisionTable(
            "Sparse",
            HitPolicy.Unique,
            [new DecisionRow("High", "[Score] > 70", "\"high\"")],
            Epoch);

        TableAnalysis analysis = Simulator.Analyze(table, [Case("uncovered", 10m), Case("high", 90m)]);

        Assert.Equal(["uncovered"], analysis.UncoveredCases);
        Assert.False(analysis.IsClean);
    }

    [Fact]
    public void Analyze_CountsAFallbackHitAsAGap()
    {
        // A fallback stops the error; it does not mean the table covered the
        // input. An author reviewing coverage needs to see it either way.
        var table = new DecisionTable(
            "Sparse",
            HitPolicy.Unique,
            [new DecisionRow("High", "[Score] > 70", "\"high\"")],
            Epoch,
            null,
            "\"default\"");

        TableAnalysis analysis = Simulator.Analyze(table, [Case("uncovered", 10m)]);

        Assert.Equal(["uncovered"], analysis.UncoveredCases);
    }

    [Fact]
    public void Analyze_FindsOverlappingRows()
    {
        // Usually means the author did not realise two conditions could both
        // be true.
        TableAnalysis analysis = Simulator.Analyze(
            Tiering(), [Case("both", 90m)]);

        Assert.Single(analysis.OverlappingCases);
        Assert.Contains("Medium, High", analysis.OverlappingCases[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_FindsARowNoCaseReached()
    {
        // Either the sample is thin, or the row is dead — and a dead row means
        // a rule someone believes is in force is not.
        TableAnalysis analysis = Simulator.Analyze(Tiering(), [Case("low", 10m)]);

        Assert.Contains("Medium", analysis.UnreachableRows);
        Assert.Contains("High", analysis.UnreachableRows);
    }

    [Fact]
    public void Analyze_UnderFirstPolicy_DoesNotReportRowsAfterTheMatchAsUnreachable()
    {
        // Under First the engine stops at the first match, so later rows were
        // never evaluated. Reporting them would be a false finding, and false
        // findings are how a report gets ignored.
        TableAnalysis analysis = Simulator.Analyze(
            Tiering(HitPolicy.First), [Case("low", 10m)]);

        Assert.Empty(analysis.UnreachableRows);
    }

    [Fact]
    public void Analyze_SeparatesAnEvaluationFailureFromAGap()
    {
        // Collapsing the two would hide whichever is rarer.
        var table = new DecisionTable(
            "Broken",
            HitPolicy.First,
            [new DecisionRow("Divide", "TRUE", "1 / ([Score] - [Score])")],
            Epoch);

        TableAnalysis analysis = Simulator.Analyze(table, [Case("any", 5m)]);

        Assert.Equal(["any"], analysis.FailedCases);
        Assert.Empty(analysis.UncoveredCases);
    }

    [Fact]
    public void Analyze_OfACompleteTable_IsClean()
    {
        var table = new DecisionTable(
            "Complete",
            HitPolicy.First,
            [
                new DecisionRow("High", "[Score] > 70", "\"high\""),
                new DecisionRow("Rest", "TRUE", "\"rest\""),
            ],
            Epoch);

        TableAnalysis analysis = Simulator.Analyze(table, [Case("high", 90m), Case("low", 10m)]);

        Assert.True(analysis.IsClean);
    }

    [Fact]
    public void Compare_ReportsWhichCasesDecideDifferently()
    {
        // The question an author actually has before changing a live pricing
        // table. A text diff cannot answer it: two rewritten conditions can be
        // equivalent, and one changed constant can move thousands of cases.
        var before = new DecisionTable(
            "Tiering",
            HitPolicy.First,
            [
                new DecisionRow("High", "[Score] > 70", "\"high\""),
                new DecisionRow("Rest", "TRUE", "\"rest\""),
            ],
            Epoch);

        var after = new DecisionTable(
            "Tiering",
            HitPolicy.First,
            [
                new DecisionRow("High", "[Score] > 50", "\"high\""),
                new DecisionRow("Rest", "TRUE", "\"rest\""),
            ],
            Epoch);

        IReadOnlyList<string> changed = Simulator.Compare(
            before, after, [Case("well-below", 10m), Case("between", 60m), Case("well-above", 90m)]);

        Assert.Equal(["between"], changed);
    }

    [Fact]
    public void Compare_OfAnEquivalentRewrite_ReportsNoChange()
    {
        // The other half of the same question: a rewrite that reads completely
        // differently but decides identically must not look like a change.
        var before = new DecisionTable(
            "T", HitPolicy.First,
            [new DecisionRow("R", "[Score] > 50", "\"a\""), new DecisionRow("Rest", "TRUE", "\"b\"")],
            Epoch);

        var after = new DecisionTable(
            "T", HitPolicy.First,
            [
                new DecisionRow("R", "NOT([Score] <= 50)", "\"a\""),
                new DecisionRow("Rest", "TRUE", "\"b\""),
            ],
            Epoch);

        Assert.Empty(Simulator.Compare(
            before, after, [Case("a", 10m), Case("b", 50m), Case("c", 51m), Case("d", 90m)]));
    }

    [Fact]
    public void Simulation_LeavesNoTrace()
    {
        // Side-effect free by construction: evaluation reads through
        // IFormulaContext and writes nothing, and the formula engine has no
        // I/O to perform even if it wanted to (ADR-026). Running the same
        // simulation twice must give identical results.
        DecisionTable table = Tiering(HitPolicy.First);
        SimulationCase[] cases = [Case("a", 10m), Case("b", 90m)];

        IReadOnlyList<SimulationResult> first = Simulator.Run(table, cases);
        IReadOnlyList<SimulationResult> second = Simulator.Run(table, cases);

        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Outcome!.Value, second[i].Outcome!.Value);
        }
    }
}
