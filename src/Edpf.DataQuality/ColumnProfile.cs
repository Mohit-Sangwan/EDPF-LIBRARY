using System;
using System.Collections.Generic;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.DataQuality;

/// <summary>
/// One observed value and how often it occurred (Phase 23d).
/// </summary>
public sealed class ValueFrequency
{
    /// <summary>Initializes a frequency entry.</summary>
    /// <param name="value">The value, or the redaction marker.</param>
    /// <param name="count">How many rows carried it.</param>
    public ValueFrequency(string value, int count)
    {
        Value = Guard.NotNull(value, nameof(value));
        Count = count;
    }

    /// <summary>The value, or the redaction marker when the column is classified.</summary>
    public string Value { get; }

    /// <summary>How many rows carried it.</summary>
    public int Count { get; }
}

/// <summary>
/// What a profile of one column found (Phase 23d).
/// </summary>
/// <remarks>
/// <para>
/// **A profile of a classified column is a disclosure route, and an easy one
/// to miss.** A "ten most common values" report over a medical record number
/// column *is* the medical record numbers. A distribution over a rare
/// diagnosis in a small cohort re-identifies the patients who have it. The
/// report is not metadata about the data — for a classified column it is a
/// projection of the data itself.
/// </para>
/// <para>
/// So a profile separates two kinds of finding. **Aggregate statistics** —
/// row count, null rate, distinct count, min and max length — describe the
/// column's shape and disclose nothing about any individual. **Value samples**
/// disclose content, and are withheld for any classified column, with
/// <see cref="ValuesWithheld"/> recording that they were.
/// </para>
/// </remarks>
public sealed class ColumnProfile
{
    /// <summary>Initializes a profile.</summary>
    /// <param name="columnName">The column profiled.</param>
    /// <param name="classification">The column's classification.</param>
    /// <param name="rowCount">Rows examined.</param>
    /// <param name="nullCount">Rows with no value.</param>
    /// <param name="distinctCount">Distinct non-null values.</param>
    /// <param name="minLength">Shortest non-null value.</param>
    /// <param name="maxLength">Longest non-null value.</param>
    /// <param name="topValues">The most frequent values, or redaction markers.</param>
    /// <param name="valuesWithheld">Whether value samples were withheld.</param>
    /// <param name="inferredPattern">A pattern the values conform to, when one was found.</param>
    public ColumnProfile(
        string columnName,
        DataClassificationLevel classification,
        int rowCount,
        int nullCount,
        int distinctCount,
        int minLength,
        int maxLength,
        IReadOnlyList<ValueFrequency> topValues,
        bool valuesWithheld,
        string? inferredPattern)
    {
        ColumnName = Guard.NotNullOrWhiteSpace(columnName, nameof(columnName));
        Classification = classification;
        RowCount = rowCount;
        NullCount = nullCount;
        DistinctCount = distinctCount;
        MinLength = minLength;
        MaxLength = maxLength;
        TopValues = Guard.NotNull(topValues, nameof(topValues));
        ValuesWithheld = valuesWithheld;
        InferredPattern = inferredPattern;
    }

    /// <summary>The column profiled.</summary>
    public string ColumnName { get; }

    /// <summary>The column's classification.</summary>
    public DataClassificationLevel Classification { get; }

    /// <summary>Rows examined.</summary>
    public int RowCount { get; }

    /// <summary>Rows with no value.</summary>
    public int NullCount { get; }

    /// <summary>Distinct non-null values.</summary>
    public int DistinctCount { get; }

    /// <summary>Shortest non-null value, or 0 when the column is empty.</summary>
    public int MinLength { get; }

    /// <summary>Longest non-null value, or 0 when the column is empty.</summary>
    public int MaxLength { get; }

    /// <summary>
    /// The most frequent values, or redaction markers when
    /// <see cref="ValuesWithheld"/> is set.
    /// </summary>
    public IReadOnlyList<ValueFrequency> TopValues { get; }

    /// <summary>
    /// True when value samples were withheld because the column is classified
    /// or because a sample would have identified too few rows.
    /// </summary>
    /// <remarks>
    /// Recorded rather than silently omitted. A reader seeing an empty
    /// <see cref="TopValues"/> would conclude the column is empty; a reader
    /// seeing this flag knows the profile is complete and the values are not
    /// theirs to see.
    /// </remarks>
    public bool ValuesWithheld { get; }

    /// <summary>
    /// A pattern the non-null values conform to, when one was found.
    /// </summary>
    /// <remarks>
    /// Expressed in a coarse shape alphabet — <c>A</c> for a letter,
    /// <c>9</c> for a digit — rather than a regular expression built from the
    /// data. A pattern derived too tightly from a classified column is itself
    /// a disclosure: a "pattern" matching exactly one value is that value.
    /// </remarks>
    public string? InferredPattern { get; }

    /// <summary>The proportion of rows with no value, from 0 to 1.</summary>
    public decimal NullRate => RowCount == 0 ? 0m : (decimal)NullCount / RowCount;

    /// <summary>
    /// The proportion of non-null values that are distinct, from 0 to 1.
    /// </summary>
    /// <remarks>
    /// A value of 1 on a column that should repeat suggests a key where a
    /// category was expected; a value near 0 on a column that should be unique
    /// suggests duplication.
    /// </remarks>
    public decimal Cardinality
    {
        get
        {
            int populated = RowCount - NullCount;
            return populated == 0 ? 0m : (decimal)DistinctCount / populated;
        }
    }
}
