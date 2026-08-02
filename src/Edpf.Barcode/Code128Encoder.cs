using System;
using System.Collections.Generic;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Barcode;

/// <summary>
/// Encodes data as Code 128 / GS1-128 symbol values (Phase 17c).
/// </summary>
/// <remarks>
/// <para>
/// Produces the sequence of **symbol values** — start code, data, checksum,
/// stop — not an image. That split is deliberate: the symbol values are the
/// part where a mistake mislabels a specimen, and they can be verified
/// exactly against the standard's own worked examples. Rendering them as bars
/// is a presentation concern with a different failure mode and a different
/// place to live (Phase 24f, where the printers are).
/// </para>
/// <para>
/// GS1-128 is Code 128 with FNC1 in the first data position. That single
/// character is what tells a scanner the payload is an element string rather
/// than free text — omit it and a perfectly formed GS1 payload is delivered to
/// the application as an undifferentiated string.
/// </para>
/// </remarks>
public static class Code128Encoder
{
    /// <summary>Symbol value for FNC1.</summary>
    public const int Fnc1 = 102;

    /// <summary>Symbol value for START A.</summary>
    public const int StartA = 103;

    /// <summary>Symbol value for START B.</summary>
    public const int StartB = 104;

    /// <summary>Symbol value for START C.</summary>
    public const int StartC = 105;

    /// <summary>Symbol value for the stop pattern.</summary>
    public const int Stop = 106;

    /// <summary>Symbol value for CODE B, used to switch out of subset C.</summary>
    public const int CodeB = 100;

    /// <summary>Symbol value for CODE C, used to switch into the numeric subset.</summary>
    public const int CodeC = 99;

    /// <summary>
    /// Encodes a GS1 element string as GS1-128 symbol values.
    /// </summary>
    /// <param name="elementString">The element string, separators included.</param>
    /// <returns>The symbol values including start, checksum and stop, or a failure.</returns>
    public static Result<IReadOnlyList<int>> EncodeGs1(string elementString)
    {
        Guard.NotNull(elementString, nameof(elementString));

        if (elementString.Length == 0)
        {
            return Result.Failure<IReadOnlyList<int>>(new Error(
                ErrorCodes.ValidationFailed, "There is nothing to encode.", ErrorCategory.Validation));
        }

        // Start B then FNC1: the FNC1 in the first data position is what marks
        // this as GS1-128 rather than ordinary Code 128.
        var values = new List<int> { StartB, Fnc1 };
        bool inSubsetC = false;

        int position = 0;
        while (position < elementString.Length)
        {
            char c = elementString[position];

            if (c == Gs1ElementString.GroupSeparator)
            {
                if (inSubsetC)
                {
                    values.Add(CodeB);
                    inSubsetC = false;
                }

                values.Add(Fnc1);
                position++;
                continue;
            }

            // Four or more digits is where subset C starts paying: it packs
            // two digits per symbol, so a 14-digit GTIN costs seven symbols
            // instead of fourteen. On a specimen label that is the difference
            // between fitting and not.
            int digitRun = CountDigits(elementString, position);

            if (!inSubsetC && digitRun >= 4 && digitRun % 2 == 0)
            {
                values.Add(CodeC);
                inSubsetC = true;
            }

            if (inSubsetC)
            {
                if (digitRun >= 2)
                {
                    values.Add(((elementString[position] - '0') * 10) + (elementString[position + 1] - '0'));
                    position += 2;
                    continue;
                }

                values.Add(CodeB);
                inSubsetC = false;
            }

            Result<int> symbol = SubsetBValue(c);
            if (symbol.IsFailure)
            {
                return Result.Failure<IReadOnlyList<int>>(symbol.Error!);
            }

            values.Add(symbol.Value);
            position++;
        }

        values.Add(Checksum(values));
        values.Add(Stop);

        return Result.Success<IReadOnlyList<int>>(values);
    }

    /// <summary>
    /// Computes the Code 128 modulo-103 checksum.
    /// </summary>
    /// <param name="values">The start code and data values, without checksum or stop.</param>
    /// <returns>The checksum symbol value.</returns>
    /// <remarks>
    /// Position-weighted: the start code counts once, then each data symbol is
    /// weighted by its 1-based position. The weighting is what makes the
    /// checksum catch transposed symbols, which an unweighted sum would not.
    /// </remarks>
    public static int Checksum(IReadOnlyList<int> values)
    {
        Guard.NotNull(values, nameof(values));

        if (values.Count == 0)
        {
            throw new ArgumentException("A checksum needs at least a start code.", nameof(values));
        }

        long sum = values[0];
        for (int i = 1; i < values.Count; i++)
        {
            sum += (long)values[i] * i;
        }

        return (int)(sum % 103);
    }

    private static int CountDigits(string data, int from)
    {
        int count = 0;
        for (int i = from; i < data.Length && char.IsDigit(data[i]); i++)
        {
            count++;
        }

        return count;
    }

    private static Result<int> SubsetBValue(char c)
    {
        // Subset B covers ASCII 32..126, mapping to symbol values 0..94.
        if (c < ' ' || c > '~')
        {
            return Result.Failure<int>(new Error(
                ErrorCodes.ValidationFailed,
                $"Code 128 subset B cannot represent the character at code point {(int)c}.",
                ErrorCategory.Validation));
        }

        return Result.Success(c - ' ');
    }
}
