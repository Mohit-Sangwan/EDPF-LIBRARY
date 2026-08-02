using System;
using System.Collections.Generic;
using Edpf.Core.Guards;

namespace Edpf.Migration;

/// <summary>Why two datasets differ at one key (Phase 35b).</summary>
public enum DifferenceKind
{
    /// <summary>Present in the source, absent from the target.</summary>
    MissingFromTarget = 0,

    /// <summary>Present in the target, absent from the source.</summary>
    UnexpectedInTarget = 1,

    /// <summary>Present in both, with different content.</summary>
    ContentDiffers = 2,
}

/// <summary>One difference, named by key and never by value (Phase 35b).</summary>
public sealed class RecordDifference
{
    /// <summary>Initializes a difference.</summary>
    /// <param name="key">The record's business key.</param>
    /// <param name="kind">Why it differs.</param>
    public RecordDifference(string key, DifferenceKind kind)
    {
        Key = Guard.NotNullOrWhiteSpace(key, nameof(key));
        Kind = kind;
    }

    /// <summary>
    /// The record's business key.
    /// </summary>
    /// <remarks>
    /// The key is carried because a difference nobody can locate is a
    /// difference nobody can fix. **Values are not**, and that asymmetry is
    /// deliberate: a reconciliation report is emailed, ticketed and archived
    /// exactly like the quality reports ADR-028 governs, and it must not
    /// become a second uncontrolled copy of the data.
    /// </remarks>
    public string Key { get; }

    /// <summary>Why it differs.</summary>
    public DifferenceKind Kind { get; }

    /// <inheritdoc />
    public override string ToString() => Kind + ": " + Key;
}

/// <summary>
/// What a reconciliation found (Phase 35b).
/// </summary>
public sealed class ReconciliationReport
{
    /// <summary>Initializes a report.</summary>
    /// <param name="sourceCount">Records examined in the source.</param>
    /// <param name="targetCount">Records examined in the target.</param>
    /// <param name="matched">Records present and identical in both.</param>
    /// <param name="differences">Every difference found.</param>
    /// <param name="ignoredFields">Fields excluded from comparison, and why.</param>
    public ReconciliationReport(
        int sourceCount,
        int targetCount,
        int matched,
        IReadOnlyList<RecordDifference> differences,
        IReadOnlyList<string> ignoredFields)
    {
        SourceCount = sourceCount;
        TargetCount = targetCount;
        Matched = matched;
        Differences = Guard.NotNull(differences, nameof(differences));
        IgnoredFields = Guard.NotNull(ignoredFields, nameof(ignoredFields));
    }

    /// <summary>Records examined in the source.</summary>
    public int SourceCount { get; }

    /// <summary>Records examined in the target.</summary>
    public int TargetCount { get; }

    /// <summary>Records present and identical in both.</summary>
    public int Matched { get; }

    /// <summary>Every difference found.</summary>
    public IReadOnlyList<RecordDifference> Differences { get; }

    /// <summary>
    /// Fields excluded from comparison, with the justification given.
    /// </summary>
    /// <remarks>
    /// Reported prominently because **what a reconciliation did not check is
    /// the first thing an auditor asks about**, and a clean report over three
    /// compared fields out of forty is not evidence of anything.
    /// </remarks>
    public IReadOnlyList<string> IgnoredFields { get; }

    /// <summary>Whether the datasets are equivalent under the declared rules.</summary>
    public bool IsEquivalent => Differences.Count == 0;

    /// <summary>
    /// A summary safe to put in a ticket.
    /// </summary>
    /// <returns>The summary.</returns>
    public override string ToString()
    {
        string verdict = IsEquivalent ? "equivalent" : $"{Differences.Count} difference(s)";
        string caveat = IgnoredFields.Count == 0
            ? string.Empty
            : $" — {IgnoredFields.Count} field(s) excluded from comparison";

        return $"source {SourceCount}, target {TargetCount}, matched {Matched}: {verdict}{caveat}";
    }
}

/// <summary>
/// Proves two datasets equivalent without holding or disclosing their values
/// (Phase 35b).
/// </summary>
/// <remarks>
/// <para>
/// The blocker to brownfield adoption is rarely the new system. It is the
/// inability to demonstrate that the new one holds the same data as the old
/// one, to somebody who will be accountable if it does not.
/// </para>
/// <para>
/// **Row counts are not that demonstration.** Two datasets with identical
/// counts can have every value swapped between rows and still agree on count,
/// sum, min and max. Equivalence is a per-record property.
/// </para>
/// </remarks>
public sealed class Reconciler
{
    private readonly IReadOnlyList<FieldComparison> _comparisons;

    /// <summary>Initializes a reconciler.</summary>
    /// <param name="comparisons">The per-field comparison rules.</param>
    /// <exception cref="ArgumentException">No field is actually compared.</exception>
    public Reconciler(IReadOnlyList<FieldComparison> comparisons)
    {
        _comparisons = Guard.NotNull(comparisons, nameof(comparisons));

        bool anyCompared = false;
        foreach (FieldComparison comparison in comparisons)
        {
            if (comparison.Canonicalization != FieldCanonicalization.Ignored)
            {
                anyCompared = true;
                break;
            }
        }

        if (!anyCompared)
        {
            // A reconciliation comparing nothing reports equivalence for any
            // two datasets, which is the most dangerous possible output: a
            // clean report that means nothing, signed off by someone who
            // believed it.
            throw new ArgumentException(
                "Every field is ignored, so this reconciliation would report any two datasets as "
                + "equivalent.",
                nameof(comparisons));
        }
    }

    /// <summary>
    /// Compares a source dataset against a target.
    /// </summary>
    /// <param name="source">Key to field values, from the legacy system.</param>
    /// <param name="target">Key to field values, from the new system.</param>
    /// <returns>The report.</returns>
    /// <remarks>
    /// Differences are reported in key order so two runs produce a diffable
    /// report. A report that reorders between runs cannot be used to show
    /// that yesterday's differences were fixed.
    /// </remarks>
    public ReconciliationReport Compare(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> source,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> target)
    {
        Guard.NotNull(source, nameof(source));
        Guard.NotNull(target, nameof(target));

        var differences = new List<RecordDifference>();
        int matched = 0;

        foreach (KeyValuePair<string, IReadOnlyDictionary<string, string?>> entry in source)
        {
            if (!target.TryGetValue(entry.Key, out IReadOnlyDictionary<string, string?>? targetValues))
            {
                differences.Add(new RecordDifference(entry.Key, DifferenceKind.MissingFromTarget));
                continue;
            }

            Abstractions.Primitives.Result<RecordFingerprint> sourcePrint =
                RecordFingerprint.Compute(entry.Key, entry.Value, _comparisons);
            Abstractions.Primitives.Result<RecordFingerprint> targetPrint =
                RecordFingerprint.Compute(entry.Key, targetValues, _comparisons);

            // A record that cannot be fingerprinted on either side counts as
            // differing. Skipping it would let a schema mismatch reconcile
            // silently, which is the failure this exists to catch.
            if (sourcePrint.IsFailure || targetPrint.IsFailure
                || !sourcePrint.Value.Equals(targetPrint.Value))
            {
                differences.Add(new RecordDifference(entry.Key, DifferenceKind.ContentDiffers));
                continue;
            }

            matched++;
        }

        foreach (KeyValuePair<string, IReadOnlyDictionary<string, string?>> entry in target)
        {
            // Extra rows in the target matter as much as missing ones. A
            // migration that duplicated a batch produces a target that
            // contains everything the source has, and a count check that only
            // looks for absences passes it.
            if (!source.ContainsKey(entry.Key))
            {
                differences.Add(new RecordDifference(entry.Key, DifferenceKind.UnexpectedInTarget));
            }
        }

        differences.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

        var ignored = new List<string>();
        foreach (FieldComparison comparison in _comparisons)
        {
            if (comparison.Canonicalization == FieldCanonicalization.Ignored)
            {
                ignored.Add($"{comparison.FieldName} ({comparison.Justification})");
            }
        }

        ignored.Sort(StringComparer.Ordinal);

        return new ReconciliationReport(source.Count, target.Count, matched, differences, ignored);
    }
}
