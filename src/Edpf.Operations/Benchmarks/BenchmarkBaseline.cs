using System;
using System.Collections.Generic;
using System.Globalization;
using Edpf.Core.Guards;

namespace Edpf.Operations.Benchmarks;

/// <summary>One recorded benchmark measurement.</summary>
public sealed class BenchmarkMeasurement
{
    /// <summary>
    /// Initializes a measurement.
    /// </summary>
    /// <param name="name">The benchmark's stable name.</param>
    /// <param name="meanNanoseconds">Mean duration.</param>
    /// <param name="allocatedBytes">Bytes allocated per operation.</param>
    /// <exception cref="ArgumentException">The name is blank or a value is negative.</exception>
    public BenchmarkMeasurement(string name, double meanNanoseconds, long allocatedBytes)
    {
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name));

        if (meanNanoseconds < 0 || allocatedBytes < 0)
        {
            throw new ArgumentException("Benchmark measurements cannot be negative.", nameof(meanNanoseconds));
        }

        MeanNanoseconds = meanNanoseconds;
        AllocatedBytes = allocatedBytes;
    }

    /// <summary>The benchmark's stable name.</summary>
    public string Name { get; }

    /// <summary>Mean duration in nanoseconds.</summary>
    public double MeanNanoseconds { get; }

    /// <summary>Bytes allocated per operation.</summary>
    public long AllocatedBytes { get; }
}

/// <summary>
/// Compares a benchmark run against the stored baseline and fails the build
/// on regression (Z.9, Phase 31).
/// </summary>
/// <remarks>
/// <para>
/// **What this prevents.** Without a gate, each phase costs two or three
/// percent — never enough to notice in review, never enough to argue about —
/// and thirty phases later the framework is quietly twice as slow as it was
/// at Phase 08, with no single commit to blame. Performance is lost the way
/// weight is gained.
/// </para>
/// <para>
/// **Allocation is gated as well as time**, and often matters more: a change
/// that allocates twice as much may benchmark identically on an idle machine
/// and then fall over under sustained load when the GC cannot keep up. The
/// Phase 00 NFR sheet budgets allocation per request for exactly this reason.
/// </para>
/// <para>
/// **A missing baseline entry is not a pass.** A renamed benchmark silently
/// loses its history, so an unknown name is reported rather than ignored.
/// </para>
/// </remarks>
public sealed class BenchmarkBaseline
{
    /// <summary>The regression tolerance from Z.9: over 5% fails the build.</summary>
    public const double RegressionTolerance = 0.05;

    private readonly IReadOnlyDictionary<string, BenchmarkMeasurement> _baseline;

    /// <summary>
    /// Initializes the gate over a stored baseline.
    /// </summary>
    /// <param name="baseline">The recorded measurements, by name.</param>
    public BenchmarkBaseline(IReadOnlyDictionary<string, BenchmarkMeasurement> baseline)
        => _baseline = Guard.NotNull(baseline, nameof(baseline));

    /// <summary>
    /// Compares a run against the baseline.
    /// </summary>
    /// <param name="current">The measurements from this run.</param>
    /// <returns>Every regression and every unrecognised benchmark.</returns>
    public IReadOnlyList<BenchmarkFinding> Compare(IReadOnlyCollection<BenchmarkMeasurement> current)
    {
        Guard.NotNull(current, nameof(current));

        var findings = new List<BenchmarkFinding>();

        foreach (BenchmarkMeasurement measurement in current)
        {
            if (!_baseline.TryGetValue(measurement.Name, out BenchmarkMeasurement? recorded))
            {
                findings.Add(new BenchmarkFinding(
                    measurement.Name,
                    BenchmarkFindingKind.NoBaseline,
                    0,
                    "No baseline entry. A renamed benchmark loses its history silently, so this is reported "
                    + "rather than treated as a pass."));
                continue;
            }

            AddIfRegressed(
                findings,
                measurement.Name,
                BenchmarkFindingKind.TimeRegression,
                recorded.MeanNanoseconds,
                measurement.MeanNanoseconds,
                "ns");

            AddIfRegressed(
                findings,
                measurement.Name,
                BenchmarkFindingKind.AllocationRegression,
                recorded.AllocatedBytes,
                measurement.AllocatedBytes,
                "bytes");
        }

        return findings;
    }

    private static void AddIfRegressed(
        List<BenchmarkFinding> findings,
        string name,
        BenchmarkFindingKind kind,
        double baseline,
        double observed,
        string unit)
    {
        if (baseline <= 0)
        {
            // Nothing to regress against; a zero baseline usually means the
            // benchmark was new when it was recorded.
            return;
        }

        double change = (observed - baseline) / baseline;
        if (change <= RegressionTolerance)
        {
            return;
        }

        findings.Add(new BenchmarkFinding(
            name,
            kind,
            change,
            string.Format(
                CultureInfo.InvariantCulture,
                "{0:P1} slower than baseline ({1:N0} → {2:N0} {3}); tolerance is {4:P0}.",
                change, baseline, observed, unit, RegressionTolerance)));
    }
}

/// <summary>What kind of benchmark finding this is.</summary>
public enum BenchmarkFindingKind
{
    /// <summary>Duration regressed beyond tolerance.</summary>
    TimeRegression = 0,

    /// <summary>Allocation regressed beyond tolerance.</summary>
    AllocationRegression = 1,

    /// <summary>The benchmark has no baseline entry — probably renamed.</summary>
    NoBaseline = 2,
}

/// <summary>One benchmark regression or anomaly.</summary>
public sealed class BenchmarkFinding
{
    /// <summary>
    /// Initializes a finding.
    /// </summary>
    /// <param name="benchmarkName">Which benchmark.</param>
    /// <param name="kind">What kind of finding.</param>
    /// <param name="changeFraction">Relative change, e.g. 0.12 for 12% slower.</param>
    /// <param name="detail">A message stating both numbers, so the finding is actionable without a rerun.</param>
    public BenchmarkFinding(
        string benchmarkName, BenchmarkFindingKind kind, double changeFraction, string detail)
    {
        BenchmarkName = Guard.NotNullOrWhiteSpace(benchmarkName, nameof(benchmarkName));
        Kind = kind;
        ChangeFraction = changeFraction;
        Detail = Guard.NotNull(detail, nameof(detail));
    }

    /// <summary>Which benchmark.</summary>
    public string BenchmarkName { get; }

    /// <summary>What kind of finding.</summary>
    public BenchmarkFindingKind Kind { get; }

    /// <summary>Relative change.</summary>
    public double ChangeFraction { get; }

    /// <summary>The actionable detail.</summary>
    public string Detail { get; }

    /// <summary>Formats as <c>name: detail</c>.</summary>
    public override string ToString() => BenchmarkName + ": " + Detail;
}
