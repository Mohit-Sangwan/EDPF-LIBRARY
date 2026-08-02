using System;
using System.Collections.Generic;
using System.Text;
using Edpf.Abstractions.Metadata;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;
using Edpf.Core.Guards;
using Edpf.Metadata;

namespace Edpf.DataQuality;

/// <summary>
/// Profiles a column's shape without disclosing its contents (Phase 23d).
/// </summary>
/// <remarks>
/// <para>
/// Profiling is how data quality stops being an opinion — *"NABH and NABL both
/// assess data quality; without measurement there is no way to demonstrate
/// it."* But the measurement itself has to be safe to hold, because a quality
/// report is exactly the kind of artefact that gets emailed, pasted into a
/// ticket and left on a dashboard.
/// </para>
/// <para>
/// Two rules therefore apply to every profile, and both come from the
/// classification the Phase 05b metadata already carries:
/// </para>
/// <list type="number">
/// <item>
/// **A classified column discloses no values.** Aggregate statistics still
/// come back — null rate and cardinality are what a data steward actually
/// needs — but the sample values do not.
/// </item>
/// <item>
/// **A value identifying fewer than <see cref="MinimumCellSize"/> rows
/// discloses nothing either, whatever the column's classification.** This is
/// small-cell suppression: in a cohort of 400, "1 patient has this postcode"
/// identifies that patient even though a postcode alone is not PHI.
/// </item>
/// </list>
/// </remarks>
public sealed class DataProfiler
{
    /// <summary>
    /// The fewest rows a value may identify and still be reported.
    /// </summary>
    /// <remarks>
    /// Five is the threshold most disclosure-control guidance converges on for
    /// published aggregates. It is a convention rather than a proof, and the
    /// right number for a given release is a decision for whoever signs it
    /// off — which is why this is a constructor argument and not a constant.
    /// </remarks>
    public const int DefaultMinimumCellSize = 5;

    /// <summary>The marker substituted for a withheld value.</summary>
    public const string WithheldMarker = "[WITHHELD]";

    private readonly IDataProtectionPolicy _policy;
    private readonly int _topValueCount;

    /// <summary>Initializes a profiler.</summary>
    /// <param name="minimumCellSize">The fewest rows a value may identify and still be reported.</param>
    /// <param name="topValueCount">How many frequent values to report.</param>
    /// <param name="policy">The classification-to-protection policy.</param>
    public DataProfiler(
        int minimumCellSize = DefaultMinimumCellSize,
        int topValueCount = 10,
        IDataProtectionPolicy? policy = null)
    {
        MinimumCellSize = Guard.Positive(minimumCellSize, nameof(minimumCellSize));
        _topValueCount = Guard.Positive(topValueCount, nameof(topValueCount));
        _policy = policy ?? ProtectionPolicy.Default;
    }

    /// <summary>The fewest rows a value may identify and still be reported.</summary>
    public int MinimumCellSize { get; }

    /// <summary>
    /// Profiles one column.
    /// </summary>
    /// <param name="field">The column's metadata, which carries its classification.</param>
    /// <param name="values">The observed values; <see langword="null"/> for absent.</param>
    /// <returns>The profile.</returns>
    public ColumnProfile Profile(IFieldMetadata field, IReadOnlyList<string?> values)
    {
        Guard.NotNull(field, nameof(field));
        Guard.NotNull(values, nameof(values));

        // The same question every other subsystem asks, answered by the same
        // table (ADR-025). A profiler with its own opinion about what counts
        // as sensitive would drift from the redactor's.
        bool classified = _policy
            .For(field.Classification)
            .HasFlagSet(DataProtectionRequirements.RedactInDiagnostics);

        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
        int nullCount = 0;
        int minLength = int.MaxValue;
        int maxLength = 0;

        foreach (string? value in values)
        {
            if (string.IsNullOrEmpty(value))
            {
                nullCount++;
                continue;
            }

            frequencies.TryGetValue(value!, out int count);
            frequencies[value!] = count + 1;

            minLength = Math.Min(minLength, value!.Length);
            maxLength = Math.Max(maxLength, value.Length);
        }

        if (minLength == int.MaxValue)
        {
            minLength = 0;
        }

        return new ColumnProfile(
            field.Name,
            field.Classification,
            values.Count,
            nullCount,
            frequencies.Count,
            minLength,
            maxLength,
            TopValues(frequencies, classified),
            classified || AnySuppressed(frequencies),
            classified ? null : InferPattern(frequencies.Keys));
    }

    private List<ValueFrequency> TopValues(
        Dictionary<string, int> frequencies, bool classified)
    {
        var ordered = new List<KeyValuePair<string, int>>(frequencies);

        // Descending by count, then by value, so a tie does not reorder
        // between runs — a profile that shuffles is a profile nobody can diff
        // against last week's.
        ordered.Sort((a, b) =>
        {
            int byCount = b.Value.CompareTo(a.Value);
            return byCount != 0 ? byCount : string.CompareOrdinal(a.Key, b.Key);
        });

        var top = new List<ValueFrequency>();

        foreach (KeyValuePair<string, int> entry in ordered)
        {
            if (top.Count >= _topValueCount)
            {
                break;
            }

            // Small-cell suppression applies regardless of classification: in
            // a cohort of 400, "one patient has this postcode" identifies that
            // patient even though a postcode alone is not PHI.
            if (entry.Value < MinimumCellSize)
            {
                continue;
            }

            top.Add(new ValueFrequency(classified ? WithheldMarker : entry.Key, entry.Value));
        }

        return top;
    }

    private bool AnySuppressed(Dictionary<string, int> frequencies)
    {
        foreach (KeyValuePair<string, int> entry in frequencies)
        {
            if (entry.Value < MinimumCellSize)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Infers a coarse shape the values conform to.
    /// </summary>
    /// <param name="values">The distinct non-null values.</param>
    /// <returns>The shape, or <see langword="null"/> when they do not agree.</returns>
    /// <remarks>
    /// The alphabet is deliberately coarse — <c>A</c> for a letter, <c>9</c>
    /// for a digit, other characters literal. A pattern derived tightly from
    /// the data is itself a disclosure: a "pattern" matching exactly one value
    /// is that value.
    /// </remarks>
    private static string? InferPattern(IEnumerable<string> values)
    {
        string? shape = null;

        foreach (string value in values)
        {
            string candidate = Shape(value);

            if (shape is null)
            {
                shape = candidate;
                continue;
            }

            if (!string.Equals(shape, candidate, StringComparison.Ordinal))
            {
                // Values disagree, so there is no single pattern. Reporting
                // the most common one would invite a validation rule that
                // rejects the legitimate minority.
                return null;
            }
        }

        return shape;
    }

    private static string Shape(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (char c in value)
        {
            builder.Append(char.IsDigit(c) ? '9' : char.IsLetter(c) ? 'A' : c);
        }

        return builder.ToString();
    }
}
