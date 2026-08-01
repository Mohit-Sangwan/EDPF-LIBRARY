using System;

namespace Edpf.DataPlatform.Classification;

/// <summary>
/// Check-digit validators for identifier formats (Phase 23 §"Automated
/// PII/PHI classification").
/// </summary>
/// <remarks>
/// <para>
/// A regular expression alone produces unusable precision: a sixteen-digit
/// order number matches a credit-card pattern, and an eleven-digit sequence
/// matches an NHS number. Check-digit validation is what turns a pattern
/// match into a classification a team will act on rather than mute.
/// </para>
/// <para>
/// These validate **structure only**. A structurally valid number is not
/// necessarily a real one, and that is the correct bar here: the classifier's
/// job is to flag data that looks like an identifier so a human decides, not
/// to confirm an identity.
/// </para>
/// </remarks>
public static class IdentifierValidators
{
    /// <summary>
    /// Luhn (mod 10) — payment cards, and many national identifiers.
    /// </summary>
    /// <param name="digits">The candidate, digits only.</param>
    /// <returns>True when the check digit is consistent.</returns>
    public static bool IsValidLuhn(string digits)
    {
        if (string.IsNullOrEmpty(digits) || digits.Length < 12)
        {
            return false;
        }

        int sum = 0;
        bool doubling = false;

        for (int i = digits.Length - 1; i >= 0; i--)
        {
            if (!char.IsDigit(digits[i]))
            {
                return false;
            }

            int value = digits[i] - '0';

            if (doubling)
            {
                value *= 2;
                if (value > 9)
                {
                    value -= 9;
                }
            }

            sum += value;
            doubling = !doubling;
        }

        return sum % 10 == 0;
    }

    /// <summary>
    /// NHS number (mod 11, weights 10..2). Ten digits; a computed remainder
    /// of 10 makes the number invalid rather than wrapping.
    /// </summary>
    /// <param name="digits">The candidate, digits only.</param>
    /// <returns>True when the check digit is consistent.</returns>
    public static bool IsValidNhsNumber(string digits)
    {
        if (string.IsNullOrEmpty(digits) || digits.Length != 10)
        {
            return false;
        }

        int sum = 0;
        for (int i = 0; i < 9; i++)
        {
            if (!char.IsDigit(digits[i]))
            {
                return false;
            }

            sum += (digits[i] - '0') * (10 - i);
        }

        if (!char.IsDigit(digits[9]))
        {
            return false;
        }

        int remainder = sum % 11;
        int check = 11 - remainder;

        if (check == 11)
        {
            check = 0;
        }

        // A check digit of 10 cannot be represented, so the number is invalid.
        return check != 10 && check == digits[9] - '0';
    }

    /// <summary>
    /// Aadhaar (Verhoeff). Twelve digits, and the first may not be 0 or 1.
    /// </summary>
    /// <param name="digits">The candidate, digits only.</param>
    /// <returns>True when the checksum is consistent.</returns>
    public static bool IsValidAadhaar(string digits)
    {
        if (string.IsNullOrEmpty(digits) || digits.Length != 12)
        {
            return false;
        }

        if (digits[0] is '0' or '1')
        {
            return false;
        }

        int checksum = 0;
        for (int i = digits.Length - 1, position = 0; i >= 0; i--, position++)
        {
            if (!char.IsDigit(digits[i]))
            {
                return false;
            }

            checksum = VerhoeffD[checksum][VerhoeffP[position % 8][digits[i] - '0']];
        }

        return checksum == 0;
    }

    /// <summary>
    /// US Social Security number structural rules: no area 000, 666 or 900+,
    /// no group 00, no serial 0000.
    /// </summary>
    /// <param name="digits">The candidate, digits only.</param>
    /// <returns>True when the number is structurally issuable.</returns>
    public static bool IsStructurallyValidSsn(string digits)
    {
        if (string.IsNullOrEmpty(digits) || digits.Length != 9)
        {
            return false;
        }

        foreach (char c in digits)
        {
            if (!char.IsDigit(c))
            {
                return false;
            }
        }

        // Digit arithmetic rather than Substring+Parse: allocation-free, and
        // it sidesteps the span-vs-string analyzer split across TFMs.
        int area = ((digits[0] - '0') * 100) + ((digits[1] - '0') * 10) + (digits[2] - '0');
        int group = ((digits[3] - '0') * 10) + (digits[4] - '0');
        int serial = ((digits[5] - '0') * 1000) + ((digits[6] - '0') * 100)
                   + ((digits[7] - '0') * 10) + (digits[8] - '0');

        return area is not 0 and not 666 and < 900 && group != 0 && serial != 0;
    }

    // Verhoeff dihedral group D5 multiplication table, and the permutation
    // table. Jagged rather than rectangular: .NET indexes jagged arrays
    // faster, and CA1814 prefers it.
    private static readonly int[][] VerhoeffD =
    [
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9],
        [1, 2, 3, 4, 0, 6, 7, 8, 9, 5],
        [2, 3, 4, 0, 1, 7, 8, 9, 5, 6],
        [3, 4, 0, 1, 2, 8, 9, 5, 6, 7],
        [4, 0, 1, 2, 3, 9, 5, 6, 7, 8],
        [5, 9, 8, 7, 6, 0, 4, 3, 2, 1],
        [6, 5, 9, 8, 7, 1, 0, 4, 3, 2],
        [7, 6, 5, 9, 8, 2, 1, 0, 4, 3],
        [8, 7, 6, 5, 9, 3, 2, 1, 0, 4],
        [9, 8, 7, 6, 5, 4, 3, 2, 1, 0],
    ];

    private static readonly int[][] VerhoeffP =
    [
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9],
        [1, 5, 7, 6, 2, 8, 3, 0, 9, 4],
        [5, 8, 0, 3, 7, 9, 6, 1, 4, 2],
        [8, 9, 1, 6, 0, 4, 3, 5, 2, 7],
        [9, 4, 5, 3, 1, 2, 6, 8, 7, 0],
        [4, 2, 8, 6, 5, 7, 3, 9, 0, 1],
        [2, 7, 9, 3, 8, 0, 6, 4, 1, 5],
        [7, 0, 4, 6, 9, 1, 3, 2, 5, 8],
    ];

    /// <summary>Strips separators so a formatted identifier can be validated.</summary>
    /// <param name="value">The candidate as written.</param>
    /// <returns>Digits only.</returns>
    public static string DigitsOnly(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsDigit(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
