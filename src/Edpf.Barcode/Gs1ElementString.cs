using System;
using System.Collections.Generic;
using System.Text;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Barcode;

/// <summary>One AI and its data (Phase 17c).</summary>
public sealed class Gs1Field
{
    /// <summary>Initializes a field.</summary>
    /// <param name="identifier">The application identifier.</param>
    /// <param name="value">The data.</param>
    public Gs1Field(Gs1ApplicationIdentifier identifier, string value)
    {
        Identifier = Guard.NotNull(identifier, nameof(identifier));
        Value = Guard.NotNull(value, nameof(value));
    }

    /// <summary>The application identifier.</summary>
    public Gs1ApplicationIdentifier Identifier { get; }

    /// <summary>The data.</summary>
    public string Value { get; }
}

/// <summary>
/// Builds and parses GS1 element strings (Phase 17c).
/// </summary>
/// <remarks>
/// <para>
/// GS1 is mandatory for medication and specimen traceability, and the
/// mandatory part is that the symbol *decodes to the fields that were
/// encoded*. Everything here exists to make that true in the cases where it
/// naively is not.
/// </para>
/// <para>
/// The separator character is <c>FNC1</c> — a Code 128 control character with
/// no ASCII equivalent. Represented here as GS (0x1D), which is what a scanner
/// transmits when it reads FNC1 in a data position, so a round-trip test
/// exercises the same bytes an application will actually receive.
/// </para>
/// </remarks>
public static class Gs1ElementString
{
    /// <summary>
    /// The separator a scanner transmits for FNC1 in a data position.
    /// </summary>
    public const char GroupSeparator = '';

    /// <summary>
    /// Builds an element string from fields.
    /// </summary>
    /// <param name="fields">The fields, in the order they should appear.</param>
    /// <returns>
    /// The element string with separators where they are required, or a
    /// failure describing which field is invalid and why.
    /// </returns>
    /// <remarks>
    /// A separator is emitted after a variable-length field **only when
    /// something follows it**. A trailing separator is not wrong, but it
    /// consumes a symbol character for nothing, and label real estate on a
    /// specimen vial is genuinely scarce.
    /// </remarks>
    public static Result<string> Build(IReadOnlyList<Gs1Field> fields)
    {
        Guard.NotNull(fields, nameof(fields));

        if (fields.Count == 0)
        {
            return Result.Failure<string>(new Error(
                ErrorCodes.ValidationFailed, "An element string needs at least one field.",
                ErrorCategory.Validation));
        }

        var builder = new StringBuilder();

        for (int i = 0; i < fields.Count; i++)
        {
            Gs1Field field = fields[i];
            Result<string> validated = Validate(field);
            if (validated.IsFailure)
            {
                return validated;
            }

            builder.Append(field.Identifier.Ai).Append(field.Value);

            if (field.Identifier.IsVariableLength && i < fields.Count - 1)
            {
                builder.Append(GroupSeparator);
            }
        }

        return Result.Success(builder.ToString());
    }

    private static Result<string> Validate(Gs1Field field)
    {
        Gs1ApplicationIdentifier ai = field.Identifier;

        if (field.Value.Length == 0)
        {
            return Result.Failure<string>(new Error(
                ErrorCodes.ValidationFailed,
                $"AI ({ai.Ai}) {ai.Name} has no value.",
                ErrorCategory.Validation));
        }

        if (!ai.IsVariableLength && field.Value.Length != ai.FixedLength)
        {
            return Result.Failure<string>(new Error(
                ErrorCodes.ValidationFailed,
                $"AI ({ai.Ai}) {ai.Name} is a fixed-length field of {ai.FixedLength} characters; "
                + $"{field.Value.Length} were supplied. A short fixed-length field would silently absorb "
                + "the characters of whatever follows it.",
                ErrorCategory.Validation));
        }

        if (field.Value.Length > ai.MaxLength)
        {
            return Result.Failure<string>(new Error(
                ErrorCodes.ValidationFailed,
                $"AI ({ai.Ai}) {ai.Name} allows at most {ai.MaxLength} characters.",
                ErrorCategory.Validation));
        }

        foreach (char c in field.Value)
        {
            // A separator inside a value would end the field early, and the
            // remainder would be parsed as a new AI. This is the injection
            // equivalent for barcodes, and it is refused rather than escaped
            // — there is no escape sequence for FNC1 to escape it to.
            if (c == GroupSeparator)
            {
                return Result.Failure<string>(new Error(
                    ErrorCodes.ValidationFailed,
                    $"AI ({ai.Ai}) {ai.Name} contains a group separator, which would terminate the field "
                    + "early and cause everything after it to be read as a different field.",
                    ErrorCategory.Validation));
            }

            if (ai.NumericOnly && !char.IsDigit(c))
            {
                return Result.Failure<string>(new Error(
                    ErrorCodes.ValidationFailed,
                    $"AI ({ai.Ai}) {ai.Name} accepts digits only.",
                    ErrorCategory.Validation));
            }

            if (c < ' ' || c > '~')
            {
                return Result.Failure<string>(new Error(
                    ErrorCodes.ValidationFailed,
                    $"AI ({ai.Ai}) {ai.Name} contains a character Code 128 cannot represent.",
                    ErrorCategory.Validation));
            }
        }

        return Result.Success(field.Value);
    }

    /// <summary>
    /// Parses an element string back into fields.
    /// </summary>
    /// <param name="elementString">The scanned data.</param>
    /// <returns>The fields, or a failure.</returns>
    /// <remarks>
    /// The inverse of <see cref="Build"/>, and the reason both exist: a
    /// round-trip test is how "the symbol decodes to what was encoded" stops
    /// being an assumption.
    /// </remarks>
    public static Result<IReadOnlyList<Gs1Field>> Parse(string elementString)
    {
        Guard.NotNull(elementString, nameof(elementString));

        var fields = new List<Gs1Field>();
        int position = 0;

        while (position < elementString.Length)
        {
            if (elementString[position] == GroupSeparator)
            {
                position++;
                continue;
            }

            Gs1ApplicationIdentifier? ai = ReadIdentifier(elementString, position);
            if (ai is null)
            {
                return Result.Failure<IReadOnlyList<Gs1Field>>(new Error(
                    ErrorCodes.ValidationFailed,
                    $"No known application identifier begins at position {position}. Guessing its length "
                    + "would misread every field after it.",
                    ErrorCategory.Validation));
            }

            position += ai.Ai.Length;

            string value;
            if (ai.IsVariableLength)
            {
                int end = elementString.IndexOf(GroupSeparator, position);
                if (end < 0)
                {
                    end = elementString.Length;
                }

                value = elementString.Substring(position, end - position);
                position = end;
            }
            else
            {
                if (position + ai.FixedLength > elementString.Length)
                {
                    return Result.Failure<IReadOnlyList<Gs1Field>>(new Error(
                        ErrorCodes.ValidationFailed,
                        $"AI ({ai.Ai}) {ai.Name} needs {ai.FixedLength} characters, and the data ends first.",
                        ErrorCategory.Validation));
                }

                value = elementString.Substring(position, ai.FixedLength);
                position += ai.FixedLength;
            }

            fields.Add(new Gs1Field(ai, value));
        }

        return fields.Count == 0
            ? Result.Failure<IReadOnlyList<Gs1Field>>(new Error(
                ErrorCodes.ValidationFailed, "The data contains no fields.", ErrorCategory.Validation))
            : Result.Success<IReadOnlyList<Gs1Field>>(fields);
    }

    private static Gs1ApplicationIdentifier? ReadIdentifier(string data, int position)
    {
        // Longest match first. AI "24" does not exist but "240" and "241" do,
        // and a shortest-match reader would take "24" from "2401234", fail to
        // find it, and give up on data that is perfectly valid.
        for (int length = 4; length >= 2; length--)
        {
            if (position + length > data.Length)
            {
                continue;
            }

            Gs1ApplicationIdentifier? ai = Gs1ApplicationIdentifier.Find(data.Substring(position, length));
            if (ai is not null)
            {
                return ai;
            }
        }

        return null;
    }
}
