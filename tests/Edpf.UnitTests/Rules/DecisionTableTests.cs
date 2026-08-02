using Edpf.Abstractions.Primitives;
using Edpf.Formula;
using Edpf.Rules;
using Edpf.UnitTests.Formula;

namespace Edpf.UnitTests.Rules;

/// <summary>
/// Phase 17c — the rules platform, promoted out of the clinical vertical into
/// core.
/// </summary>
public sealed class DecisionTableTests
{
    private static readonly DateTimeOffset Epoch = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly RuleEngine Engine = new();

    private static DecisionTable Table(
        HitPolicy policy, string? fallback = null, params DecisionRow[] rows)
        => new("Tiering", policy, rows, Epoch, null, fallback);

    private static TestFormulaContext Case(decimal score)
        => TestFormulaContext.WithValues(("Score", FormulaValue.FromNumber(score)));

    // ── the reason there is no default hit policy ──────────────────────────

    [Fact]
    public void UniquePolicy_WithOverlappingRows_IsRefusedRatherThanTiebroken()
    {
        // A table whose author has not said what happens when two rows match
        // will one day return whichever was first. In a pricing or triage
        // table that is a wrong answer delivered confidently — so the policy
        // is a required, closed choice, and Unique treats the overlap as the
        // authoring error it is.
        DecisionTable table = Table(
            HitPolicy.Unique,
            null,
            new DecisionRow("High", "[Score] > 50", "\"high\""),
            new DecisionRow("VeryHigh", "[Score] > 80", "\"very-high\""));

        Result<RuleOutcome> result = Engine.Evaluate(table, Case(90m));

        Assert.True(result.IsFailure);
        Assert.Contains("High, VeryHigh", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstPolicy_TakesTheFirstMatchInDeclarationOrder()
    {
        DecisionTable table = Table(
            HitPolicy.First,
            null,
            new DecisionRow("High", "[Score] > 50", "\"high\""),
            new DecisionRow("VeryHigh", "[Score] > 80", "\"very-high\""));

        Assert.Equal("high", Engine.Evaluate(table, Case(90m)).Value.Value.Text);
    }

    [Fact]
    public void PriorityPolicy_TakesTheHighestPriorityMatch()
    {
        DecisionTable table = Table(
            HitPolicy.Priority,
            null,
            new DecisionRow("High", "[Score] > 50", "\"high\"", priority: 1),
            new DecisionRow("VeryHigh", "[Score] > 80", "\"very-high\"", priority: 2));

        Assert.Equal("very-high", Engine.Evaluate(table, Case(90m)).Value.Value.Text);
    }

    [Fact]
    public void PriorityPolicy_WithTiedPriorities_IsRefused()
    {
        // The same ambiguity Unique rejects, wearing a different hat: which
        // row wins would depend on declaration order.
        DecisionTable table = Table(
            HitPolicy.Priority,
            null,
            new DecisionRow("A", "[Score] > 50", "\"a\"", priority: 5),
            new DecisionRow("B", "[Score] > 80", "\"b\"", priority: 5));

        Result<RuleOutcome> result = Engine.Evaluate(table, Case(90m));

        Assert.True(result.IsFailure);
        Assert.Contains("priority 5", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectPolicy_GathersEveryMatch()
    {
        DecisionTable table = Table(
            HitPolicy.Collect,
            null,
            new DecisionRow("A", "[Score] > 10", "\"a\""),
            new DecisionRow("B", "[Score] > 20", "\"b\""),
            new DecisionRow("C", "[Score] > 900", "\"c\""));

        RuleOutcome outcome = Engine.Evaluate(table, Case(50m)).Value;

        Assert.Equal(2, outcome.CollectedValues.Count);
        Assert.Equal(["A", "B"], outcome.MatchedRows);
    }

    // ── gaps ───────────────────────────────────────────────────────────────

    [Fact]
    public void NoMatchWithNoFallback_IsAnError_NotAnEmptyResult()
    {
        // A caller reading an absent result will interpret it, and the usual
        // interpretation is zero — free in a pricing table, none in a dosage
        // table. So the gap is reported.
        DecisionTable table = Table(
            HitPolicy.Unique, null, new DecisionRow("High", "[Score] > 50", "\"high\""));

        Result<RuleOutcome> result = Engine.Evaluate(table, Case(10m));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.NotFound, result.Error!.Code);
        Assert.Contains("gap", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoMatchWithAFallback_UsesItAndSaysSo()
    {
        DecisionTable table = Table(
            HitPolicy.Unique, "\"standard\"", new DecisionRow("High", "[Score] > 50", "\"high\""));

        RuleOutcome outcome = Engine.Evaluate(table, Case(10m)).Value;

        Assert.Equal("standard", outcome.Value.Text);
        Assert.True(outcome.UsedFallback);
    }

    // ── explanation ────────────────────────────────────────────────────────

    [Fact]
    public void Outcome_NamesTheRowsThatMatched()
    {
        // Not a debugging aid. Someone will be asked why this claim was
        // denied, and "the table said so" does not survive an appeal.
        DecisionTable table = Table(
            HitPolicy.First, null, new DecisionRow("HighRisk", "[Score] > 50", "\"deny\""));

        RuleOutcome outcome = Engine.Evaluate(table, Case(90m)).Value;

        Assert.Equal(["HighRisk"], outcome.MatchedRows);
    }

    [Fact]
    public void FailingRow_IsNamedInTheError()
    {
        DecisionTable table = Table(
            HitPolicy.First, null, new DecisionRow("Ratio", "TRUE", "[Score] / 0"));

        Result<RuleOutcome> result = Engine.Evaluate(table, Case(1m));

        Assert.True(result.IsFailure);
        Assert.Contains("Ratio", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonBooleanCondition_IsRefused()
    {
        // Whether the row matches would be undefined, and the engine must not
        // invent a coercion rule for it.
        DecisionTable table = Table(
            HitPolicy.First, null, new DecisionRow("Odd", "[Score] + 1", "\"x\""));

        Result<RuleOutcome> result = Engine.Evaluate(table, Case(1m));

        Assert.True(result.IsFailure);
        Assert.Contains("yes-or-no", result.Error!.Message, StringComparison.Ordinal);
    }

    // ── registration and versioning ────────────────────────────────────────

    [Fact]
    public void UnparseableRow_IsRefusedAtRegistration()
    {
        // A condition that fails to parse when a claim run reaches it has
        // already stopped the claim run.
        var engine = new RuleEngine();

        Result result = engine.Register(Table(
            HitPolicy.First, null, new DecisionRow("Broken", "[Score] >", "\"x\"")));

        Assert.True(result.IsFailure);
        Assert.Contains("Broken", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnparseableFallback_IsRefusedAtRegistration()
    {
        var engine = new RuleEngine();

        Result result = engine.Register(Table(
            HitPolicy.First, "EVAL(\"x\")", new DecisionRow("Ok", "TRUE", "1")));

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Table_ResolvesToTheVersionInEffect()
    {
        // A claim adjudicated in 2024 must be explainable from the rules that
        // applied in 2024.
        var engine = new RuleEngine();
        DateTimeOffset y2024 = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset y2025 = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        engine.Register(new DecisionTable(
            "Tiering", HitPolicy.First, [new DecisionRow("R", "TRUE", "\"old\"")], y2024, y2025));
        engine.Register(new DecisionTable(
            "Tiering", HitPolicy.First, [new DecisionRow("R", "TRUE", "\"new\"")], y2025));

        Assert.Equal("old",
            engine.Evaluate(engine.Resolve("Tiering", y2024.AddMonths(6)).Value, Case(1m)).Value.Value.Text);
        Assert.Equal("new",
            engine.Evaluate(engine.Resolve("Tiering", y2025.AddMonths(6)).Value, Case(1m)).Value.Value.Text);
    }

    [Fact]
    public void OverlappingTableVersions_AreRefused()
    {
        var engine = new RuleEngine();
        engine.Register(Table(HitPolicy.First, null, new DecisionRow("R", "TRUE", "1")));

        Result second = engine.Register(Table(HitPolicy.First, null, new DecisionRow("R", "TRUE", "2")));

        Assert.True(second.IsFailure);
        Assert.Equal(ErrorCodes.Duplicate, second.Error!.Code);
    }

    [Fact]
    public void EmptyTable_IsRefusedAtConstruction()
    {
        Assert.Throws<ArgumentException>(() => new DecisionTable(
            "Empty", HitPolicy.First, [], Epoch));
    }

    // ── the sandbox is inherited, not reimplemented ────────────────────────

    [Fact]
    public void RuleConditions_CannotEscapeTheFormulaSandbox()
    {
        // ADR-026 names "a second expression evaluator appears anywhere in the
        // codebase" as a revisit trigger. The rules engine builds on
        // Edpf.Formula rather than beside it, so the sandbox, the decimal
        // arithmetic and the classification propagation all come along.
        var engine = new RuleEngine();

        foreach (string hostile in new[]
                 {
                     "System.IO.File.ReadAllText(\"/etc/passwd\")",
                     "EVAL(\"1=1\")",
                     "[Score].GetType()",
                 })
        {
            Result result = engine.Register(Table(
                HitPolicy.First, null, new DecisionRow("Hostile", hostile, "1")));

            Assert.True(result.IsFailure, $"'{hostile}' should not register.");
        }
    }

    [Fact]
    public void RuleOutcome_ReadingPhi_CarriesThePhiClassification()
    {
        var table = new DecisionTable(
            "Triage",
            HitPolicy.First,
            [new DecisionRow("Any", "TRUE", "[Weight] * 2")],
            Epoch);

        var context = TestFormulaContext.WithValues(
            ("Weight", FormulaValue.FromNumber(70m, DataClassificationLevel.Phi)));

        RuleOutcome outcome = Engine.Evaluate(table, context).Value;

        Assert.Equal(DataClassificationLevel.Phi, outcome.Value.Classification);
    }

    [Fact]
    public void RuleArithmetic_IsDecimal()
    {
        var table = new DecisionTable(
            "Billing", HitPolicy.First, [new DecisionRow("Any", "TRUE", "0.1 + 0.2")], Epoch);

        Assert.Equal(0.3m, Engine.Evaluate(table, Case(1m)).Value.Value.Number);
    }
}
