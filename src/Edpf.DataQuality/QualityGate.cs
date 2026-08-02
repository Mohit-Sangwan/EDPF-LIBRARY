using System;
using System.Collections.Generic;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.DataQuality;

/// <summary>What a quality gate decided about a dataset (Phase 23d).</summary>
public enum GateDecision
{
    /// <summary>Every threshold was met; the data may be ingested.</summary>
    Admit = 0,

    /// <summary>
    /// A threshold was not met; the data is held rather than ingested.
    /// </summary>
    /// <remarks>
    /// Quarantine, not rejection. Rejected data is gone, and the sender rarely
    /// notices in time to resend; quarantined data is inspectable, correctable
    /// and re-admissible.
    /// </remarks>
    Quarantine = 1,
}

/// <summary>A gate's verdict, with the reasons (Phase 23d).</summary>
public sealed class GateResult
{
    /// <summary>Initializes a verdict.</summary>
    /// <param name="decision">What the gate decided.</param>
    /// <param name="failures">Why, one entry per unmet threshold.</param>
    public GateResult(GateDecision decision, IReadOnlyList<string> failures)
    {
        Decision = decision;
        Failures = Guard.NotNull(failures, nameof(failures));
    }

    /// <summary>What the gate decided.</summary>
    public GateDecision Decision { get; }

    /// <summary>Why, one entry per unmet threshold.</summary>
    public IReadOnlyList<string> Failures { get; }

    /// <summary>Whether the data may be ingested.</summary>
    public bool Admitted => Decision == GateDecision.Admit;
}

/// <summary>
/// Holds below-threshold data out of the store (Phase 23d).
/// </summary>
/// <remarks>
/// <para>
/// *"Quality gates on import that quarantine rather than ingest
/// below-threshold data."*
/// </para>
/// <para>
/// **Quarantine, not rejection, and not ingestion-with-a-warning.** Rejecting
/// loses the data and the sender usually finds out too late. Ingesting with a
/// warning is worse: the bad data is now indistinguishable from the good, and
/// every downstream consumer inherits it — a warning in a log is not a control.
/// </para>
/// <para>
/// Thresholds are per dimension rather than one overall number, for the reason
/// <see cref="QualityScore.WeakestScore"/> exists: a dataset that is perfectly
/// complete and entirely invalid must not average its way past a gate.
/// </para>
/// </remarks>
public sealed class QualityGate
{
    private readonly Dictionary<QualityDimension, decimal> _thresholds = [];

    /// <summary>Initializes a gate.</summary>
    /// <param name="name">The gate name, for the quarantine record.</param>
    public QualityGate(string name) => Name = Guard.NotNullOrWhiteSpace(name, nameof(name));

    /// <summary>The gate name.</summary>
    public string Name { get; }

    /// <summary>
    /// Requires a dimension to score at least <paramref name="threshold"/>.
    /// </summary>
    /// <param name="dimension">The dimension.</param>
    /// <param name="threshold">The minimum score, from 0 to 1.</param>
    /// <returns>This gate, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The threshold is outside 0 to 1.</exception>
    public QualityGate Require(QualityDimension dimension, decimal threshold)
    {
        if (threshold < 0m || threshold > 1m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(threshold), threshold, "A threshold must be between 0 and 1.");
        }

        _thresholds[dimension] = threshold;
        return this;
    }

    /// <summary>
    /// Decides whether a dataset may be ingested.
    /// </summary>
    /// <param name="score">The dataset's quality score.</param>
    /// <returns>The verdict, with a reason for every unmet threshold.</returns>
    /// <remarks>
    /// A dimension the gate requires but the score does not assess is a
    /// failure, not a pass. Treating an unmeasured dimension as satisfied
    /// would let a gate be bypassed by simply not running the check — which is
    /// the easiest bypass there is, and the one that looks like an accident.
    /// </remarks>
    public GateResult Evaluate(QualityScore score)
    {
        Guard.NotNull(score, nameof(score));

        var failures = new List<string>();

        foreach (KeyValuePair<QualityDimension, decimal> requirement in _thresholds)
        {
            Result<DimensionScore> assessed = score.For(requirement.Key);

            if (assessed.IsFailure)
            {
                failures.Add(
                    $"{requirement.Key} is required at {requirement.Value:P0} but was not assessed. An "
                    + "unmeasured dimension cannot be assumed to pass.");
                continue;
            }

            if (assessed.Value.Score < requirement.Value)
            {
                failures.Add(
                    $"{requirement.Key} scored {assessed.Value.Score:P1} against a threshold of "
                    + $"{requirement.Value:P0} ({assessed.Value.Method}).");
            }
        }

        return new GateResult(
            failures.Count == 0 ? GateDecision.Admit : GateDecision.Quarantine, failures);
    }
}
