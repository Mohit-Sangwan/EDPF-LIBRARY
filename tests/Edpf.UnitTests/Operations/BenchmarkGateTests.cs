using Edpf.Operations.Benchmarks;

namespace Edpf.UnitTests.Operations;

/// <summary>
/// Z.9 and Phase 31: benchmarks run against the stored baseline, and a >5%
/// regression fails the build. Without it, each phase costs two or three
/// percent and thirty phases later the framework is quietly twice as slow,
/// with no single commit to blame.
/// </summary>
public sealed class BenchmarkGateTests
{
    private static BenchmarkBaseline Baseline => new(
        new Dictionary<string, BenchmarkMeasurement>(StringComparer.Ordinal)
        {
            ["RepositoryRead"] = new("RepositoryRead", meanNanoseconds: 1_000, allocatedBytes: 512),
            ["EncryptField"] = new("EncryptField", meanNanoseconds: 800, allocatedBytes: 256),
        });

    [Fact]
    public void Compare_WithinTolerance_ReportsNothing()
    {
        // 4% slower is noise, not a regression.
        IReadOnlyList<BenchmarkFinding> findings = Baseline.Compare(
        [
            new BenchmarkMeasurement("RepositoryRead", 1_040, 512),
        ]);

        Assert.Empty(findings);
    }

    [Fact]
    public void Compare_TimeRegressionBeyondTolerance_IsReported()
    {
        // 12% slower.
        IReadOnlyList<BenchmarkFinding> findings = Baseline.Compare(
        [
            new BenchmarkMeasurement("RepositoryRead", 1_120, 512),
        ]);

        BenchmarkFinding finding = Assert.Single(findings);
        Assert.Equal(BenchmarkFindingKind.TimeRegression, finding.Kind);
        Assert.Equal(0.12, finding.ChangeFraction, precision: 4);
    }

    [Fact]
    public void Compare_AllocationRegression_IsReportedSeparately()
    {
        // Allocation often matters more than time: a change that allocates
        // twice as much can benchmark identically on an idle machine and then
        // fall over under load when the GC cannot keep up.
        IReadOnlyList<BenchmarkFinding> findings = Baseline.Compare(
        [
            new BenchmarkMeasurement("EncryptField", 800, 1_024),
        ]);

        BenchmarkFinding finding = Assert.Single(findings);
        Assert.Equal(BenchmarkFindingKind.AllocationRegression, finding.Kind);
    }

    [Fact]
    public void Compare_BothRegressed_ReportsBoth()
    {
        IReadOnlyList<BenchmarkFinding> findings = Baseline.Compare(
        [
            new BenchmarkMeasurement("RepositoryRead", 2_000, 2_048),
        ]);

        Assert.Equal(2, findings.Count);
    }

    [Fact]
    public void Compare_Improvement_IsNotAFinding()
    {
        IReadOnlyList<BenchmarkFinding> findings = Baseline.Compare(
        [
            new BenchmarkMeasurement("RepositoryRead", 500, 128),
        ]);

        Assert.Empty(findings);
    }

    [Fact]
    public void Compare_RenamedBenchmark_IsReportedNotSilentlyPassed()
    {
        // A renamed benchmark loses its history. Treating an unknown name as
        // a pass is how a hot path stops being watched without anyone
        // deciding to stop watching it.
        IReadOnlyList<BenchmarkFinding> findings = Baseline.Compare(
        [
            new BenchmarkMeasurement("RepositoryReadV2", 5_000, 4_096),
        ]);

        BenchmarkFinding finding = Assert.Single(findings);
        Assert.Equal(BenchmarkFindingKind.NoBaseline, finding.Kind);
    }

    [Fact]
    public void Compare_Finding_StatesBothNumbersSoItIsActionable()
    {
        IReadOnlyList<BenchmarkFinding> findings = Baseline.Compare(
        [
            new BenchmarkMeasurement("RepositoryRead", 1_500, 512),
        ]);

        string detail = findings[0].Detail;

        Assert.Contains("1,000", detail, StringComparison.Ordinal);
        Assert.Contains("1,500", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Tolerance_MatchesTheStandard()
    {
        // Z.9 states 5%. Pinned so relaxing it is a visible decision.
        Assert.Equal(0.05, BenchmarkBaseline.RegressionTolerance);
    }

    [Fact]
    public void Measurement_NegativeValue_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new BenchmarkMeasurement("x", -1, 0));
        Assert.Throws<ArgumentException>(() => new BenchmarkMeasurement("x", 0, -1));
    }
}
