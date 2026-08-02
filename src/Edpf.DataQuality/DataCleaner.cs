using System;
using System.Collections.Generic;
using Edpf.Abstractions.Metadata;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;
using Edpf.Core.Guards;

namespace Edpf.DataQuality;

/// <summary>
/// A record of one value being changed by cleansing (Phase 23d).
/// </summary>
/// <remarks>
/// <para>
/// *"Cleansing clinical data is a change to the medical record and must be
/// traceable."*
/// </para>
/// <para>
/// Standardising an address or trimming a name looks like housekeeping. It is
/// an amendment to a record a clinician may later rely on, made by a process
/// rather than a person — which makes it *more* in need of a trail, not less.
/// If a cleansing rule turns out to have been wrong, the only way back is a
/// record of what each value was before.
/// </para>
/// </remarks>
public sealed class CleansingRecord
{
    /// <summary>Initializes a record.</summary>
    /// <param name="rowKey">Identifies the row changed.</param>
    /// <param name="fieldName">The field changed.</param>
    /// <param name="ruleName">The rule that changed it.</param>
    /// <param name="before">The value before.</param>
    /// <param name="after">The value after.</param>
    /// <param name="classification">The field's classification.</param>
    /// <param name="changedUtc">When.</param>
    public CleansingRecord(
        string rowKey,
        string fieldName,
        string ruleName,
        string? before,
        string? after,
        DataClassificationLevel classification,
        DateTimeOffset changedUtc)
    {
        RowKey = Guard.NotNullOrWhiteSpace(rowKey, nameof(rowKey));
        FieldName = Guard.NotNullOrWhiteSpace(fieldName, nameof(fieldName));
        RuleName = Guard.NotNullOrWhiteSpace(ruleName, nameof(ruleName));
        Before = before;
        After = after;
        Classification = classification;
        ChangedUtc = changedUtc;
    }

    /// <summary>Identifies the row changed.</summary>
    public string RowKey { get; }

    /// <summary>The field changed.</summary>
    public string FieldName { get; }

    /// <summary>The rule that changed it.</summary>
    public string RuleName { get; }

    /// <summary>
    /// The value before the change.
    /// </summary>
    /// <remarks>
    /// **Held in full, including for classified fields.** A before/after trail
    /// that redacts the before is not a trail — it cannot be used to reverse
    /// the change, which is the reason it exists. The trail therefore inherits
    /// the field's classification and belongs in storage protected to the same
    /// level, which is what <see cref="Classification"/> is for.
    /// </remarks>
    public string? Before { get; }

    /// <summary>The value after the change.</summary>
    public string? After { get; }

    /// <summary>
    /// The classification of the field changed, and therefore of this record.
    /// </summary>
    public DataClassificationLevel Classification { get; }

    /// <summary>When.</summary>
    public DateTimeOffset ChangedUtc { get; }
}

/// <summary>One cleansing transformation (Phase 23d).</summary>
public sealed class CleansingRule
{
    /// <summary>Initializes a rule.</summary>
    /// <param name="name">The rule name, recorded against every change it makes.</param>
    /// <param name="fieldName">The field it applies to.</param>
    /// <param name="transform">The transformation; returns the same value to make no change.</param>
    public CleansingRule(string name, string fieldName, Func<string?, string?> transform)
    {
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name));
        FieldName = Guard.NotNullOrWhiteSpace(fieldName, nameof(fieldName));
        Transform = Guard.NotNull(transform, nameof(transform));
    }

    /// <summary>The rule name.</summary>
    public string Name { get; }

    /// <summary>The field it applies to.</summary>
    public string FieldName { get; }

    /// <summary>The transformation.</summary>
    public Func<string?, string?> Transform { get; }
}

/// <summary>
/// Applies cleansing rules and records every change (Phase 23d).
/// </summary>
public sealed class DataCleaner
{
    private readonly IClock _clock;
    private readonly List<CleansingRecord> _trail = [];

    /// <summary>Initializes a cleaner.</summary>
    /// <param name="clock">Time source (Z.3 rule 4).</param>
    public DataCleaner(IClock clock) => _clock = Guard.NotNull(clock, nameof(clock));

    /// <summary>Every change made, in order.</summary>
    public IReadOnlyList<CleansingRecord> Trail => _trail;

    /// <summary>
    /// Applies a rule to one value.
    /// </summary>
    /// <param name="rule">The rule.</param>
    /// <param name="field">The field's metadata, which carries its classification.</param>
    /// <param name="rowKey">Identifies the row.</param>
    /// <param name="value">The current value.</param>
    /// <returns>The value after cleansing, or a failure.</returns>
    /// <remarks>
    /// A rule that leaves the value unchanged records nothing. A trail padded
    /// with no-ops is a trail nobody reads, and the changes that matter are
    /// the ones that get lost in it.
    /// </remarks>
    public Result<string?> Apply(
        CleansingRule rule, IFieldMetadata field, string rowKey, string? value)
    {
        Guard.NotNull(rule, nameof(rule));
        Guard.NotNull(field, nameof(field));

        if (!string.Equals(rule.FieldName, field.Name, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<string?>(new Error(
                ErrorCodes.ValidationFailed,
                $"Rule '{rule.Name}' applies to '{rule.FieldName}', not '{field.Name}'.",
                ErrorCategory.Validation));
        }

        string? cleaned;
        try
        {
            cleaned = rule.Transform(value);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // A rule that throws must not take the import down, and must not
            // leave the value half-changed. The original stands and the
            // failure is reported against the named rule.
            return Result.Failure<string?>(new Error(
                ErrorCodes.ValidationFailed,
                $"Cleansing rule '{rule.Name}' failed on field '{field.Name}'.",
                ErrorCategory.Validation));
        }

        if (string.Equals(value, cleaned, StringComparison.Ordinal))
        {
            return Result.Success(value);
        }

        _trail.Add(new CleansingRecord(
            rowKey, field.Name, rule.Name, value, cleaned, field.Classification, _clock.UtcNow));

        return Result.Success(cleaned);
    }

    /// <summary>
    /// Reverses every recorded change, most recent first.
    /// </summary>
    /// <param name="rowKey">The row to reverse.</param>
    /// <param name="fieldName">The field to reverse.</param>
    /// <returns>
    /// The value as it was before cleansing, or a failure when nothing was
    /// recorded for that row and field.
    /// </returns>
    /// <remarks>
    /// The reason the trail holds unredacted before-values. A cleansing rule
    /// that turns out to have been wrong — a name standardiser that mangles a
    /// legitimate hyphenated surname, say — has to be undoable across every
    /// row it touched.
    /// </remarks>
    public Result<string?> OriginalValue(string rowKey, string fieldName)
    {
        for (int i = 0; i < _trail.Count; i++)
        {
            CleansingRecord record = _trail[i];

            if (string.Equals(record.RowKey, rowKey, StringComparison.Ordinal)
                && string.Equals(record.FieldName, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                // The FIRST record for this row and field holds the value as
                // it arrived. Later records hold intermediate states, and
                // reversing to one of those would restore a value that was
                // itself the output of a rule.
                return Result.Success(record.Before);
            }
        }

        return Result.Failure<string?>(new Error(
            ErrorCodes.NotFound,
            $"No cleansing was recorded for '{fieldName}' on that row.",
            ErrorCategory.NotFound));
    }
}
