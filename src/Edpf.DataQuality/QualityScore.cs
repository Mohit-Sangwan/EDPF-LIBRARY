using System;
using System.Collections.Generic;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.DataQuality;

/// <summary>
/// The quality dimensions scored explicitly (Phase 23d).
/// </summary>
/// <remarks>
/// Named separately because they fail separately and are fixed differently. A
/// single "quality percentage" is worse than no number at all: it lets a
/// dataset that is 100% complete and entirely stale score the same as one that
/// is current and half-empty, and the two need opposite remedies.
/// </remarks>
public enum QualityDimension
{
    /// <summary>Values are present where they are required.</summary>
    Completeness = 0,

    /// <summary>Values match an independent source of truth.</summary>
    Accuracy = 1,

    /// <summary>Values agree with related values elsewhere.</summary>
    Consistency = 2,

    /// <summary>Values are recent enough to be acted on.</summary>
    Timeliness = 3,

    /// <summary>Entities appear once.</summary>
    Uniqueness = 4,

    /// <summary>Values conform to their declared format and range.</summary>
    Validity = 5,
}

/// <summary>One dimension's score and how it was arrived at (Phase 23d).</summary>
public sealed class DimensionScore
{
    /// <summary>Initializes a score.</summary>
    /// <param name="dimension">The dimension.</param>
    /// <param name="passed">Rows satisfying it.</param>
    /// <param name="total">Rows assessed.</param>
    /// <param name="method">How it was measured, for the reader who has to trust the number.</param>
    /// <exception cref="ArgumentOutOfRangeException">More rows passed than were assessed.</exception>
    public DimensionScore(QualityDimension dimension, int total, int passed, string method)
    {
        Dimension = dimension;
        Total = total;
        Passed = passed;
        Method = Guard.NotNullOrWhiteSpace(method, nameof(method));

        if (passed > total || passed < 0 || total < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(passed), passed, "Rows passing must be between zero and rows assessed.");
        }
    }

    /// <summary>The dimension.</summary>
    public QualityDimension Dimension { get; }

    /// <summary>Rows assessed.</summary>
    public int Total { get; }

    /// <summary>Rows satisfying the dimension.</summary>
    public int Passed { get; }

    /// <summary>
    /// How it was measured.
    /// </summary>
    /// <remarks>
    /// Carried with the number because a quality score is worthless to whoever
    /// has to act on it unless they know what was actually checked.
    /// "Accuracy 94%" means nothing; "94% matched the national register"
    /// means something, and so does "94% were non-empty", which is a much
    /// weaker claim wearing the same label.
    /// </remarks>
    public string Method { get; }

    /// <summary>
    /// The score from 0 to 1.
    /// </summary>
    /// <remarks>
    /// A dimension assessed over zero rows scores 0, not 1. An empty dataset
    /// is not a perfect one, and scoring it perfect is how a broken import
    /// passes a quality gate.
    /// </remarks>
    public decimal Score => Total == 0 ? 0m : (decimal)Passed / Total;
}

/// <summary>
/// A dataset's quality across every dimension assessed (Phase 23d).
/// </summary>
public sealed class QualityScore
{
    private readonly Dictionary<QualityDimension, DimensionScore> _scores = [];

    /// <summary>Initializes a score set.</summary>
    /// <param name="datasetName">What was assessed.</param>
    /// <param name="assessedUtc">When.</param>
    /// <param name="scores">The per-dimension scores.</param>
    /// <exception cref="ArgumentException">A dimension is scored more than once.</exception>
    public QualityScore(
        string datasetName, DateTimeOffset assessedUtc, IReadOnlyList<DimensionScore> scores)
    {
        DatasetName = Guard.NotNullOrWhiteSpace(datasetName, nameof(datasetName));
        AssessedUtc = assessedUtc;
        Guard.NotNull(scores, nameof(scores));

        foreach (DimensionScore score in scores)
        {
            if (!_scores.TryAdd(score.Dimension, score))
            {
                throw new ArgumentException(
                    $"{score.Dimension} is scored more than once. Which score applied would depend on "
                    + "ordering.",
                    nameof(scores));
            }
        }
    }

    /// <summary>What was assessed.</summary>
    public string DatasetName { get; }

    /// <summary>When.</summary>
    public DateTimeOffset AssessedUtc { get; }

    /// <summary>The dimensions assessed.</summary>
    public IReadOnlyCollection<QualityDimension> AssessedDimensions => _scores.Keys;

    /// <summary>
    /// Looks up one dimension's score.
    /// </summary>
    /// <param name="dimension">The dimension.</param>
    /// <returns>The score, or a failure when that dimension was not assessed.</returns>
    /// <remarks>
    /// A failure rather than a default, because "not assessed" and "scored
    /// zero" are opposite facts and collapsing them hides which one holds.
    /// </remarks>
    public Result<DimensionScore> For(QualityDimension dimension)
        => _scores.TryGetValue(dimension, out DimensionScore? score)
            ? Result.Success(score)
            : Result.Failure<DimensionScore>(new Error(
                ErrorCodes.NotFound,
                $"{dimension} was not assessed for '{DatasetName}'. That is not the same as scoring zero.",
                ErrorCategory.NotFound));

    /// <summary>
    /// The lowest score across the dimensions assessed.
    /// </summary>
    /// <remarks>
    /// The weakest dimension, not an average. Averaging lets a dataset that is
    /// perfectly complete and entirely invalid look acceptable, and a gate
    /// built on the average would admit it.
    /// </remarks>
    public decimal WeakestScore
    {
        get
        {
            decimal weakest = 1m;
            bool any = false;

            foreach (KeyValuePair<QualityDimension, DimensionScore> entry in _scores)
            {
                any = true;
                if (entry.Value.Score < weakest)
                {
                    weakest = entry.Value.Score;
                }
            }

            return any ? weakest : 0m;
        }
    }
}
