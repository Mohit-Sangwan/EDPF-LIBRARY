using Edpf.Abstractions.Primitives;
using Edpf.Migration;
using Edpf.UnitTests.TestDoubles;

namespace Edpf.UnitTests.Migration;

/// <summary>
/// Phase 35b — the brownfield migration kit, closing the critical risk
/// "nobody migrates off legacy".
/// </summary>
public sealed class MigrationKitTests
{
    private static readonly FieldComparison[] Exact =
    [
        new("Id"),
        new("Name"),
        new("Amount"),
    ];

    private static Dictionary<string, string?> Record(string name, string amount)
        => new(StringComparer.Ordinal) { ["Id"] = "1", ["Name"] = name, ["Amount"] = amount };

    private static Dictionary<string, IReadOnlyDictionary<string, string?>> Dataset(
        params (string Key, Dictionary<string, string?> Values)[] records)
    {
        var set = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        foreach ((string key, Dictionary<string, string?> values) in records)
        {
            set[key] = values;
        }

        return set;
    }

    // ── row counts prove nothing ───────────────────────────────────────────

    [Fact]
    public void DatasetsWithEqualCountsButSwappedValues_AreNotEquivalent()
    {
        // The reason this package exists. Two datasets with identical counts
        // can have every value swapped between rows, and count, sum, min and
        // max all agree.
        var reconciler = new Reconciler(Exact);

        var source = Dataset(("a", Record("Alice", "10")), ("b", Record("Bob", "20")));
        var target = Dataset(("a", Record("Bob", "20")), ("b", Record("Alice", "10")));

        ReconciliationReport report = reconciler.Compare(source, target);

        Assert.Equal(report.SourceCount, report.TargetCount);
        Assert.False(report.IsEquivalent);
        Assert.Equal(2, report.Differences.Count);
    }

    [Fact]
    public void IdenticalDatasets_AreEquivalent()
    {
        var reconciler = new Reconciler(Exact);
        var source = Dataset(("a", Record("Alice", "10")));

        ReconciliationReport report = reconciler.Compare(source, Dataset(("a", Record("Alice", "10"))));

        Assert.True(report.IsEquivalent);
        Assert.Equal(1, report.Matched);
    }

    [Fact]
    public void ExtraRowsInTheTarget_AreReported()
    {
        // A migration that duplicated a batch produces a target containing
        // everything the source has, and a check that only looks for absences
        // passes it.
        var reconciler = new Reconciler(Exact);

        ReconciliationReport report = reconciler.Compare(
            Dataset(("a", Record("Alice", "10"))),
            Dataset(("a", Record("Alice", "10")), ("b", Record("Bob", "20"))));

        Assert.False(report.IsEquivalent);
        Assert.Equal(DifferenceKind.UnexpectedInTarget, report.Differences[0].Kind);
    }

    [Fact]
    public void MissingRowsInTheTarget_AreReported()
    {
        var reconciler = new Reconciler(Exact);

        ReconciliationReport report = reconciler.Compare(
            Dataset(("a", Record("Alice", "10")), ("b", Record("Bob", "20"))),
            Dataset(("a", Record("Alice", "10"))));

        Assert.Equal(DifferenceKind.MissingFromTarget, report.Differences[0].Kind);
        Assert.Equal("b", report.Differences[0].Key);
    }

    // ── the report does not become a second copy of the data ───────────────

    [Fact]
    public void DifferencesNameTheKey_ButNeverTheValues()
    {
        // A reconciliation report is emailed, ticketed and archived exactly
        // like the quality reports ADR-028 governs. The key is carried
        // because a difference nobody can locate is a difference nobody can
        // fix; the values are not.
        var reconciler = new Reconciler(Exact);

        ReconciliationReport report = reconciler.Compare(
            Dataset(("a", Record("Alice", "10"))),
            Dataset(("a", Record("Bob", "99999"))));

        string rendered = string.Join("|", report.Differences) + report.ToString();

        Assert.Contains("a", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Alice", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Bob", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("99999", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void DifferencesAreSorted_SoTwoRunsProduceADiffableReport()
    {
        // A report that reorders between runs cannot be used to show that
        // yesterday's differences were fixed.
        var reconciler = new Reconciler(Exact);

        ReconciliationReport report = reconciler.Compare(
            Dataset(("c", Record("C", "1")), ("a", Record("A", "1")), ("b", Record("B", "1"))),
            Dataset());

        Assert.Equal(["a", "b", "c"], report.Differences.Select(d => d.Key).ToList());
    }

    // ── canonicalisation is declared, never guessed ────────────────────────

    [Fact]
    public void NumericCanonicalization_TreatsOnePointFiveZeroAsOnePointFive()
    {
        // Normalise too little and the report floods with false differences
        // that nobody reads.
        var reconciler = new Reconciler(
        [
            new FieldComparison("Id"),
            new FieldComparison("Name"),
            new FieldComparison(
                "Amount", FieldCanonicalization.Numeric,
                "Legacy stores fixed-point strings; the new system stores decimals."),
        ]);

        ReconciliationReport report = reconciler.Compare(
            Dataset(("a", Record("Alice", "1.50"))),
            Dataset(("a", Record("Alice", "1.5"))));

        Assert.True(report.IsEquivalent);
    }

    [Fact]
    public void NonExactComparison_RequiresAWrittenJustification()
    {
        // Every relaxation stops the reconciliation detecting a class of
        // defect. The differences you normalise away are exactly the ones you
        // will never find again.
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new FieldComparison("Amount", FieldCanonicalization.Numeric));

        Assert.Contains("Say why that is safe", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactIsTheDefault_AndNeedsNoJustification()
    {
        // The option that cannot hide a defect is the one you get by default.
        FieldComparison comparison = new("Amount");

        Assert.Equal(FieldCanonicalization.Exact, comparison.Canonicalization);
    }

    [Fact]
    public void CaseFolding_IsInvariant_NotCultureSensitive()
    {
        // Under a Turkish culture "I" lowercases to "ı", and the same two
        // records would reconcile in one region and differ in another.
        var comparison = new FieldComparison(
            "Name", FieldCanonicalization.CaseInsensitive, "Legacy upper-cases on write.");

        Assert.Equal(comparison.Canonicalize("ISTANBUL"), comparison.Canonicalize("istanbul"));
        Assert.NotEqual(comparison.Canonicalize("ISTANBUL"), comparison.Canonicalize("ıstanbul"));
    }

    [Fact]
    public void NullAndEmptyString_AreNotTheSame()
    {
        // A legacy system storing "" where the new one stores NULL is a real
        // difference, and it changes how every downstream nullable check
        // behaves.
        var comparison = new FieldComparison("Name");

        Assert.NotEqual(comparison.Canonicalize(null), comparison.Canonicalize(string.Empty));
    }

    [Fact]
    public void IgnoredFields_AreReportedProminently()
    {
        // What a reconciliation did NOT check is the first thing an auditor
        // asks about, and a clean report over three fields out of forty is not
        // evidence of anything.
        var reconciler = new Reconciler(
        [
            new FieldComparison("Id"),
            new FieldComparison("Name"),
            new FieldComparison(
                "Amount", FieldCanonicalization.Ignored,
                "Recalculated by the new system from line items."),
        ]);

        ReconciliationReport report = reconciler.Compare(
            Dataset(("a", Record("Alice", "10"))),
            Dataset(("a", Record("Alice", "999"))));

        Assert.True(report.IsEquivalent);
        Assert.Single(report.IgnoredFields);
        Assert.Contains("excluded from comparison", report.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReconcilerComparingNothing_IsRefused()
    {
        // The most dangerous possible output is a clean report that means
        // nothing, signed off by someone who believed it.
        Assert.Throws<ArgumentException>(() => new Reconciler(
            [new FieldComparison("Id", FieldCanonicalization.Ignored, "surrogate")]));
    }

    // ── fingerprints ───────────────────────────────────────────────────────

    [Fact]
    public void Fingerprint_IsIndependentOfFieldOrdering()
    {
        // Without this the same record fingerprints differently on two
        // machines and every row looks changed.
        var forward = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Id"] = "1", ["Name"] = "Alice", ["Amount"] = "10",
        };
        var reversed = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Amount"] = "10", ["Name"] = "Alice", ["Id"] = "1",
        };

        Assert.Equal(
            RecordFingerprint.Compute("a", forward, Exact).Value,
            RecordFingerprint.Compute("a", reversed, Exact).Value);
    }

    [Fact]
    public void Fingerprint_DistinguishesFieldBoundaries()
    {
        // Length-prefixed, so "ab" + "c" cannot collide with "a" + "bc" —
        // otherwise a surname shifting one character into a given name
        // reconciles cleanly.
        FieldComparison[] fields = [new("First"), new("Second")];

        var left = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["First"] = "ab", ["Second"] = "c",
        };
        var right = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["First"] = "a", ["Second"] = "bc",
        };

        Assert.NotEqual(
            RecordFingerprint.Compute("a", left, fields).Value.Digest,
            RecordFingerprint.Compute("a", right, fields).Value.Digest);
    }

    [Fact]
    public void MissingComparedField_IsRefused_NotTreatedAsEmpty()
    {
        // Treating it as empty would let a migration that dropped a column
        // reconcile cleanly against a source that still has it.
        var incomplete = new Dictionary<string, string?>(StringComparer.Ordinal) { ["Id"] = "1" };

        Result<RecordFingerprint> result = RecordFingerprint.Compute("a", incomplete, Exact);

        Assert.True(result.IsFailure);
        Assert.Contains("not an empty one", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordThatCannotBeFingerprinted_CountsAsDiffering()
    {
        // Skipping it would let a schema mismatch reconcile silently, which is
        // the failure this exists to catch.
        var reconciler = new Reconciler(Exact);

        ReconciliationReport report = reconciler.Compare(
            Dataset(("a", Record("Alice", "10"))),
            Dataset(("a", new Dictionary<string, string?>(StringComparer.Ordinal) { ["Id"] = "1" })));

        Assert.False(report.IsEquivalent);
        Assert.Equal(DifferenceKind.ContentDiffers, report.Differences[0].Kind);
    }

    // ── cutover ────────────────────────────────────────────────────────────

    private static ReconciliationReport CleanReport()
        => new Reconciler(Exact).Compare(
            Dataset(("a", Record("Alice", "10"))), Dataset(("a", Record("Alice", "10"))));

    private static ReconciliationReport DirtyReport()
        => new Reconciler(Exact).Compare(
            Dataset(("a", Record("Alice", "10"))), Dataset(("a", Record("Bob", "10"))));

    private static CutoverPlan PlanAt(CutoverStage stage)
    {
        var plan = new CutoverPlan("billing", new FakeClock());

        while (plan.Stage < stage)
        {
            plan.Advance(CleanReport(), "migration-lead");
        }

        return plan;
    }

    [Fact]
    public void Stages_AdvanceOneAtATime()
    {
        // Skipping from Backfilled to serving reads means the new system has
        // never been observed to stay in step under live write traffic, which
        // is the only thing dual-write is for.
        var plan = new CutoverPlan("billing", new FakeClock());

        plan.Advance(null, "lead");
        Assert.Equal(CutoverStage.Backfilled, plan.Stage);

        plan.Advance(null, "lead");
        Assert.Equal(CutoverStage.DualWrite, plan.Stage);
    }

    [Fact]
    public void ServingReadsFromTheNewSystem_RequiresAReconciliation()
    {
        CutoverPlan plan = PlanAt(CutoverStage.DualWrite);

        Result result = plan.Advance(null, "lead");

        Assert.True(result.IsFailure);
        Assert.Contains("requires a reconciliation", result.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(CutoverStage.DualWrite, plan.Stage);
    }

    [Fact]
    public void AdvancingWithAnUnresolvedDifference_IsRefused()
    {
        CutoverPlan plan = PlanAt(CutoverStage.DualWrite);

        Result result = plan.Advance(DirtyReport(), "lead");

        Assert.True(result.IsFailure);
        Assert.Equal(CutoverStage.DualWrite, plan.Stage);
    }

    [Fact]
    public void EveryStageBeforeRetirement_IsReversible()
    {
        CutoverPlan plan = PlanAt(CutoverStage.NewSystemReads);

        Assert.True(plan.IsReversible);
        Assert.True(plan.Reverse("latency regression", "lead").IsSuccess);
        Assert.Equal(CutoverStage.DualWrite, plan.Stage);
    }

    [Fact]
    public void ReversalRequiresAReason()
    {
        CutoverPlan plan = PlanAt(CutoverStage.DualWrite);

        Assert.True(plan.Reverse("   ", "lead").IsFailure);
    }

    [Fact]
    public void RetiringLegacy_CannotBeReachedByAdvancing()
    {
        // An irreversible step that looks identical to a reversible one will
        // eventually be taken by someone who thought it was reversible.
        CutoverPlan plan = PlanAt(CutoverStage.NewSystemReads);

        Result result = plan.Advance(CleanReport(), "lead");

        Assert.True(result.IsFailure);
        Assert.Contains("point of no return", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RetiringLegacy_RequiresTheExactAcknowledgement()
    {
        CutoverPlan plan = PlanAt(CutoverStage.NewSystemReads);

        Assert.True(plan.RetireLegacy(CleanReport(), "yes", "lead").IsFailure);
        Assert.True(plan.RetireLegacy(
            CleanReport(), CutoverPlan.RequiredAcknowledgement, "lead").IsSuccess);
        Assert.Equal(CutoverStage.LegacyRetired, plan.Stage);
    }

    [Fact]
    public void AfterRetirement_ReversalIsRefusedAndSaysWhy()
    {
        // Stated rather than attempted. A reversal that half-succeeds after
        // legacy stops being written leaves two partial systems and no source
        // of truth.
        CutoverPlan plan = PlanAt(CutoverStage.NewSystemReads);
        plan.RetireLegacy(CleanReport(), CutoverPlan.RequiredAcknowledgement, "lead");

        Result result = plan.Reverse("we changed our minds", "lead");

        Assert.True(result.IsFailure);
        Assert.False(plan.IsReversible);
        Assert.Contains("restoring it from backup", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RetiringWithAnUnresolvedDifference_IsRefused()
    {
        // After this step the legacy copy stops being updated, so an
        // unresolved difference becomes permanent.
        CutoverPlan plan = PlanAt(CutoverStage.NewSystemReads);

        Assert.True(plan.RetireLegacy(
            DirtyReport(), CutoverPlan.RequiredAcknowledgement, "lead").IsFailure);
    }

    [Fact]
    public void EveryStageChange_IsLogged()
    {
        CutoverPlan plan = PlanAt(CutoverStage.NewSystemReads);
        plan.Reverse("latency regression", "steward-04");

        Assert.Contains(plan.Log, entry => entry.Contains("latency regression", StringComparison.Ordinal));
        Assert.Contains(plan.Log, entry => entry.Contains("steward-04", StringComparison.Ordinal));
    }
}
