using System;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Barcode;

/// <summary>
/// GS1 check-digit calculation and verification (Phase 17c).
/// </summary>
/// <remarks>
/// <para>
/// The check digit is the last line of defence against a mistyped or
/// misprinted identifier. A GTIN keyed in by hand with two digits transposed
/// is a different product; on a medication label it is a different drug.
/// </para>
/// <para>
/// The GS1 modulo-10 scheme weights digits 3 and 1 alternately from the right.
/// That weighting is what catches transpositions — an unweighted sum would
/// give <c>1234</c> and <c>2134</c> the same check digit, and transposition is
/// the most common keying error there is.
/// </para>
/// </remarks>
public static class CheckDigit
{
    /// <summary>
    /// Computes the GS1 modulo-10 check digit for the payload.
    /// </summary>
    /// <param name="payload">The digits *without* the check digit.</param>
    /// <returns>The check digit, or a failure if the payload is not digits.</returns>
    public static Result<int> ComputeMod10(string payload)
    {
        Guard.NotNull(payload, nameof(payload));

        if (payload.Length == 0)
        {
            return Result.Failure<int>(new Error(
                ErrorCodes.ValidationFailed, "A check digit needs a payload.", ErrorCategory.Validation));
        }

        int sum = 0;

        // Weights alternate 3,1 from the RIGHTMOST payload digit, not from the
        // left. Anchoring at the left gives the wrong weighting for any
        // odd-length payload, and GTIN-13 payloads are odd-length — a
        // left-anchored implementation passes its GTIN-14 tests and fails in
        // production.
        for (int i = 0; i < payload.Length; i++)
        {
            char c = payload[payload.Length - 1 - i];
            if (!char.IsDigit(c))
            {
                return Result.Failure<int>(new Error(
                    ErrorCodes.ValidationFailed,
                    "A check digit can only be computed over digits.",
                    ErrorCategory.Validation));
            }

            sum += (c - '0') * (i % 2 == 0 ? 3 : 1);
        }

        return Result.Success((10 - (sum % 10)) % 10);
    }

    /// <summary>
    /// Verifies that a complete identifier's final digit is its check digit.
    /// </summary>
    /// <param name="identifier">The complete identifier, check digit included.</param>
    /// <returns>Whether it is valid.</returns>
    public static bool IsValidMod10(string? identifier)
    {
        if (string.IsNullOrEmpty(identifier) || identifier!.Length < 2)
        {
            return false;
        }

        Result<int> expected = ComputeMod10(identifier.Substring(0, identifier.Length - 1));
        if (expected.IsFailure)
        {
            return false;
        }

        char last = identifier[identifier.Length - 1];
        return char.IsDigit(last) && (last - '0') == expected.Value;
    }

    /// <summary>
    /// Appends the check digit to a payload.
    /// </summary>
    /// <param name="payload">The digits without the check digit.</param>
    /// <returns>The complete identifier, or a failure.</returns>
    public static Result<string> Append(string payload)
    {
        Result<int> digit = ComputeMod10(payload);
        return digit.IsFailure
            ? Result.Failure<string>(digit.Error!)
            : Result.Success(payload + digit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
