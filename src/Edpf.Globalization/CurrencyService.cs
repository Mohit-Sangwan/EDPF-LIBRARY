using System;
using System.Collections.Generic;
using System.Globalization;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Globalization;

/// <summary>
/// A monetary amount: a <see cref="decimal"/> and an ISO 4217 code, never a
/// bare number (Phase 27 §"Currency"; Z.3 rule 14).
/// </summary>
public readonly struct Money : IEquatable<Money>
{
    /// <summary>
    /// Initializes an amount.
    /// </summary>
    /// <param name="amount">The magnitude.</param>
    /// <param name="currencyCode">ISO 4217 alphabetic code, e.g. <c>JPY</c>.</param>
    /// <exception cref="ArgumentException">The code is not three uppercase letters.</exception>
    public Money(decimal amount, string currencyCode)
    {
        Guard.NotNullOrWhiteSpace(currencyCode, nameof(currencyCode));

        if (currencyCode.Length != 3)
        {
            throw new ArgumentException("ISO 4217 codes are exactly three letters.", nameof(currencyCode));
        }

        foreach (char c in currencyCode)
        {
            if (c is < 'A' or > 'Z')
            {
                throw new ArgumentException("ISO 4217 codes are uppercase letters.", nameof(currencyCode));
            }
        }

        Amount = amount;
        CurrencyCode = currencyCode;
    }

    /// <summary>The magnitude. Always <see cref="decimal"/> — never a float.</summary>
    public decimal Amount { get; }

    /// <summary>The ISO 4217 code.</summary>
    public string CurrencyCode { get; }

    /// <inheritdoc />
    public bool Equals(Money other)
        => Amount == other.Amount
        && string.Equals(CurrencyCode, other.CurrencyCode, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Money other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        unchecked
        {
            return (Amount.GetHashCode() * 397)
                 ^ (CurrencyCode is null ? 0 : StringComparer.Ordinal.GetHashCode(CurrencyCode));
        }
    }

    /// <summary>Value equality.</summary>
    public static bool operator ==(Money left, Money right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(Money left, Money right) => !left.Equals(right);

    /// <summary>Formats as <c>AMOUNT CODE</c> using the invariant culture.</summary>
    public override string ToString()
        => Amount.ToString(CultureInfo.InvariantCulture) + " " + CurrencyCode;
}

/// <summary>
/// ISO 4217 minor-unit handling (Phase 27 §"Currency").
/// </summary>
/// <remarks>
/// <para>
/// **Hardcoding two decimal places is a defect.** JPY and KRW have zero minor
/// units; BHD, KWD and OMR have three. Rounding a Kuwaiti dinar to two places
/// loses a factor of ten, and formatting yen with decimals is simply wrong.
/// </para>
/// <para>
/// Two currencies are never added. A <see cref="Money"/> in JPY plus a
/// <see cref="Money"/> in USD is not a number — it is a question about an
/// exchange rate on a particular date, and this type refuses to guess.
/// </para>
/// </remarks>
public sealed class CurrencyService
{
    // Currencies whose minor units are not two. Anything absent is two, which
    // is the actual ISO 4217 majority — the exceptions are the point.
    private static readonly Dictionary<string, int> MinorUnitExceptions = new(StringComparer.Ordinal)
    {
        ["JPY"] = 0,
        ["KRW"] = 0,
        ["VND"] = 0,
        ["CLP"] = 0,
        ["ISK"] = 0,
        ["PYG"] = 0,
        ["RWF"] = 0,
        ["UGX"] = 0,
        ["XAF"] = 0,
        ["XOF"] = 0,
        ["BHD"] = 3,
        ["KWD"] = 3,
        ["OMR"] = 3,
        ["JOD"] = 3,
        ["TND"] = 3,
        ["IQD"] = 3,
        ["LYD"] = 3,
    };

    /// <summary>
    /// The number of decimal places this currency uses.
    /// </summary>
    /// <param name="currencyCode">ISO 4217 code.</param>
    /// <returns>0, 2 or 3 depending on the currency.</returns>
    public static int MinorUnits(string currencyCode)
    {
        Guard.NotNullOrWhiteSpace(currencyCode, nameof(currencyCode));

        return MinorUnitExceptions.TryGetValue(currencyCode, out int units) ? units : 2;
    }

    /// <summary>
    /// Rounds to the currency's minor unit using banker's rounding.
    /// </summary>
    /// <param name="money">The amount.</param>
    /// <returns>The rounded amount, in the same currency.</returns>
    /// <remarks>
    /// <see cref="MidpointRounding.ToEven"/> rather than away-from-zero:
    /// repeated half-up rounding across many transactions introduces a
    /// systematic upward bias, which an auditor will eventually find.
    /// </remarks>
    public static Money Round(Money money)
        => new(Math.Round(money.Amount, MinorUnits(money.CurrencyCode), MidpointRounding.ToEven),
               money.CurrencyCode);

    /// <summary>
    /// Adds two amounts.
    /// </summary>
    /// <param name="left">First amount.</param>
    /// <param name="right">Second amount.</param>
    /// <returns>
    /// The sum, or failure when the currencies differ — that is a question
    /// about an exchange rate on a date, not an addition.
    /// </returns>
    public static Result<Money> Add(Money left, Money right)
    {
        if (!string.Equals(left.CurrencyCode, right.CurrencyCode, StringComparison.Ordinal))
        {
            return Result.Failure<Money>(new Error(
                ErrorCodes.ValidationFailed,
                $"Cannot add {left.CurrencyCode} to {right.CurrencyCode}: conversion requires a dated rate.",
                ErrorCategory.Validation));
        }

        return Result.Success(new Money(left.Amount + right.Amount, left.CurrencyCode));
    }

    /// <summary>
    /// Formats an amount for a culture, honouring the currency's minor units
    /// rather than the culture's default of two.
    /// </summary>
    /// <param name="money">The amount.</param>
    /// <param name="culture">The formatting culture.</param>
    /// <returns>The formatted string.</returns>
    public static string Format(Money money, CultureInfo culture)
    {
        Guard.NotNull(culture, nameof(culture));

        int units = MinorUnits(money.CurrencyCode);
        return money.Amount.ToString("N" + units.ToString(CultureInfo.InvariantCulture), culture)
             + " " + money.CurrencyCode;
    }
}
