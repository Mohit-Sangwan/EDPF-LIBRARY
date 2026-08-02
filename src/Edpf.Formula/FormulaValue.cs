using System;
using Edpf.Abstractions.Primitives;

namespace Edpf.Formula;

/// <summary>What a formula value holds.</summary>
public enum FormulaValueKind
{
    /// <summary>No value — an empty field, or a lookup that found nothing.</summary>
    Blank = 0,

    /// <summary>A decimal number. Never a float (Phase 08c).</summary>
    Number = 1,

    /// <summary>Text.</summary>
    Text = 2,

    /// <summary>A boolean.</summary>
    Boolean = 3,

    /// <summary>An instant.</summary>
    Timestamp = 4,
}

/// <summary>
/// A value flowing through a formula, carrying its classification
/// (Phase 08c).
/// </summary>
/// <remarks>
/// <para>
/// **Numbers are <see cref="decimal"/>, never <see cref="double"/>.** The
/// master document is explicit: *"a rounding error in a dosage or an invoice
/// is not a cosmetic defect."* Binary floating point cannot represent 0.1, so
/// a dose of 0.1 mg computed three ways gives three answers; decimal is base-10
/// and gives one.
/// </para>
/// <para>
/// **Classification travels with the value.** A formula reading a PHI field
/// produces a PHI result, because otherwise a formula is a laundering
/// mechanism: read protected data, compute something trivial like
/// <c>value * 1</c>, and emit an unclassified answer that no redactor,
/// encryptor or export filter will touch. Every operation takes the highest
/// classification among its inputs.
/// </para>
/// </remarks>
public readonly struct FormulaValue : IEquatable<FormulaValue>
{
    private FormulaValue(
        FormulaValueKind kind,
        decimal number,
        string? text,
        bool boolean,
        DateTimeOffset timestamp,
        DataClassificationLevel classification)
    {
        Kind = kind;
        Number = number;
        Text = text;
        Boolean = boolean;
        Timestamp = timestamp;
        Classification = classification;
    }

    /// <summary>The kind of value held.</summary>
    public FormulaValueKind Kind { get; }

    /// <summary>The numeric value, when <see cref="Kind"/> is Number.</summary>
    public decimal Number { get; }

    /// <summary>The text value, when <see cref="Kind"/> is Text.</summary>
    public string? Text { get; }

    /// <summary>The boolean value, when <see cref="Kind"/> is Boolean.</summary>
    public bool Boolean { get; }

    /// <summary>The instant, when <see cref="Kind"/> is Timestamp.</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>
    /// The highest classification contributing to this value.
    /// </summary>
    /// <remarks>
    /// A result must never be less classified than anything it was computed
    /// from, or the formula engine becomes a way to strip protections.
    /// </remarks>
    public DataClassificationLevel Classification { get; }

    /// <summary>The blank value.</summary>
    public static FormulaValue Blank { get; }

    /// <summary>Creates a number.</summary>
    /// <param name="value">The value.</param>
    /// <param name="classification">The classification it carries.</param>
    /// <returns>The value.</returns>
    public static FormulaValue FromNumber(
        decimal value, DataClassificationLevel classification = DataClassificationLevel.Public)
        => new(FormulaValueKind.Number, value, null, false, default, classification);

    /// <summary>Creates text.</summary>
    /// <param name="value">The value.</param>
    /// <param name="classification">The classification it carries.</param>
    /// <returns>The value.</returns>
    public static FormulaValue FromText(
        string value, DataClassificationLevel classification = DataClassificationLevel.Public)
        => new(FormulaValueKind.Text, 0m, value, false, default, classification);

    /// <summary>Creates a boolean.</summary>
    /// <param name="value">The value.</param>
    /// <param name="classification">The classification it carries.</param>
    /// <returns>The value.</returns>
    public static FormulaValue FromBoolean(
        bool value, DataClassificationLevel classification = DataClassificationLevel.Public)
        => new(FormulaValueKind.Boolean, 0m, null, value, default, classification);

    /// <summary>Creates a timestamp.</summary>
    /// <param name="value">The value.</param>
    /// <param name="classification">The classification it carries.</param>
    /// <returns>The value.</returns>
    public static FormulaValue FromTimestamp(
        DateTimeOffset value, DataClassificationLevel classification = DataClassificationLevel.Public)
        => new(FormulaValueKind.Timestamp, 0m, null, false, value, classification);

    /// <summary>
    /// Returns this value reclassified to at least <paramref name="level"/>.
    /// </summary>
    /// <param name="level">The classification to absorb.</param>
    /// <returns>The value, never less classified than before.</returns>
    public FormulaValue WithClassificationAtLeast(DataClassificationLevel level)
        => level <= Classification
            ? this
            : new FormulaValue(Kind, Number, Text, Boolean, Timestamp, level);

    /// <summary>
    /// The higher of two classifications.
    /// </summary>
    /// <param name="first">The first.</param>
    /// <param name="second">The second.</param>
    /// <returns>The higher level.</returns>
    public static DataClassificationLevel Combine(
        DataClassificationLevel first, DataClassificationLevel second)
        => first >= second ? first : second;

    /// <inheritdoc />
    public bool Equals(FormulaValue other)
        => Kind == other.Kind
            && Number == other.Number
            && string.Equals(Text, other.Text, StringComparison.Ordinal)
            && Boolean == other.Boolean
            && Timestamp == other.Timestamp
            && Classification == other.Classification;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is FormulaValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        int hash = (int)Kind;
        hash = (hash * 397) ^ Number.GetHashCode();
        hash = (hash * 397) ^ (Text?.GetHashCode(StringComparison.Ordinal) ?? 0);
        hash = (hash * 397) ^ Boolean.GetHashCode();
        hash = (hash * 397) ^ Timestamp.GetHashCode();
        return (hash * 397) ^ (int)Classification;
    }

    /// <summary>Equality.</summary>
    /// <param name="left">Left.</param>
    /// <param name="right">Right.</param>
    /// <returns>Whether they are equal.</returns>
    public static bool operator ==(FormulaValue left, FormulaValue right) => left.Equals(right);

    /// <summary>Inequality.</summary>
    /// <param name="left">Left.</param>
    /// <param name="right">Right.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(FormulaValue left, FormulaValue right) => !left.Equals(right);
}
