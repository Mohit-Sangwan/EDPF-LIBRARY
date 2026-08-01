using System;
using System.Collections.Generic;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.DataPlatform.Clinical;

/// <summary>
/// UCUM-style unit conversion with mandatory dimension checking
/// (Phase 24 §"Clinical safety").
/// </summary>
/// <remarks>
/// <para>
/// **This addresses the class of error that kills patients.** A dose recorded
/// as 5 mg and administered as 5 mcg is a thousand-fold error, and the
/// numbers look equally plausible on a screen. Unit confusion is a leading
/// cause of medication error, and the defence is that a quantity is never a
/// bare number.
/// </para>
/// <para>
/// Three rules follow, and all three fail closed:
/// </para>
/// <list type="number">
/// <item>A quantity carries its unit. There is no API taking a bare
/// <see cref="decimal"/> dose.</item>
/// <item>Conversion **across dimensions is refused**, not coerced — mass to
/// volume is a question about a substance, not an arithmetic operation.</item>
/// <item>An unknown unit is refused rather than assumed. Assuming is how a
/// typo becomes a dose.</item>
/// </list>
/// <para>
/// Arithmetic is <see cref="decimal"/> throughout: binary floating point
/// cannot represent 0.1 exactly, and a rounding artefact in a dose
/// calculation is not acceptable (Z.3 rule 14 applies the same reasoning to
/// money).
/// </para>
/// </remarks>
public sealed class UnitConverter
{
    /// <summary>Physical dimensions the converter knows about.</summary>
    public enum Dimension
    {
        /// <summary>Mass — g, mg, mcg, kg, ng.</summary>
        Mass = 0,

        /// <summary>Volume — L, mL, dL, uL.</summary>
        Volume = 1,

        /// <summary>Amount of substance — mol, mmol, umol.</summary>
        Substance = 2,

        /// <summary>Time — s, min, h, d.</summary>
        Time = 3,
    }

    private sealed class UnitDefinition(Dimension dimension, decimal factorToBase)
    {
        internal Dimension Dimension { get; } = dimension;

        /// <summary>Multiplier converting this unit to the dimension's base unit.</summary>
        internal decimal FactorToBase { get; } = factorToBase;
    }

    // Base units: gram, litre, mole, second. Case-sensitive, because UCUM is:
    // "mg" and "Mg" differ by nine orders of magnitude.
    private static readonly Dictionary<string, UnitDefinition> Units = new(StringComparer.Ordinal)
    {
        ["kg"] = new(Dimension.Mass, 1_000m),
        ["g"] = new(Dimension.Mass, 1m),
        ["mg"] = new(Dimension.Mass, 0.001m),
        ["ug"] = new(Dimension.Mass, 0.000_001m),
        ["mcg"] = new(Dimension.Mass, 0.000_001m),
        ["ng"] = new(Dimension.Mass, 0.000_000_001m),

        ["L"] = new(Dimension.Volume, 1m),
        ["dL"] = new(Dimension.Volume, 0.1m),
        ["mL"] = new(Dimension.Volume, 0.001m),
        ["uL"] = new(Dimension.Volume, 0.000_001m),

        ["mol"] = new(Dimension.Substance, 1m),
        ["mmol"] = new(Dimension.Substance, 0.001m),
        ["umol"] = new(Dimension.Substance, 0.000_001m),

        ["s"] = new(Dimension.Time, 1m),
        ["min"] = new(Dimension.Time, 60m),
        ["h"] = new(Dimension.Time, 3_600m),
        ["d"] = new(Dimension.Time, 86_400m),
    };

    /// <summary>Every unit the converter recognises.</summary>
    public static IReadOnlyCollection<string> KnownUnits => Units.Keys;

    /// <summary>
    /// Converts a quantity between units of the same dimension.
    /// </summary>
    /// <param name="value">The magnitude.</param>
    /// <param name="fromUnit">The source unit, UCUM-style and case-sensitive.</param>
    /// <param name="toUnit">The target unit.</param>
    /// <returns>
    /// The converted magnitude, or failure — <see cref="ErrorCodes.ValidationFailed"/>
    /// for an unknown unit or a dimension mismatch. **Never a best guess.**
    /// </returns>
    public static Result<decimal> Convert(decimal value, string fromUnit, string toUnit)
    {
        Guard.NotNullOrWhiteSpace(fromUnit, nameof(fromUnit));
        Guard.NotNullOrWhiteSpace(toUnit, nameof(toUnit));

        if (!Units.TryGetValue(fromUnit, out UnitDefinition? from))
        {
            return UnknownUnit(fromUnit);
        }

        if (!Units.TryGetValue(toUnit, out UnitDefinition? to))
        {
            return UnknownUnit(toUnit);
        }

        if (from.Dimension != to.Dimension)
        {
            // Mass to volume needs a density; substance to mass needs a molar
            // mass. Both are questions about a substance, and neither is
            // something a unit converter may assume.
            return Result.Failure<decimal>(new Error(
                ErrorCodes.ValidationFailed,
                $"Cannot convert {from.Dimension} to {to.Dimension}: the conversion depends on the substance.",
                ErrorCategory.Validation));
        }

        return Result.Success(value * from.FactorToBase / to.FactorToBase);
    }

    /// <summary>
    /// Checks a quantity against a reference range, refusing when the units
    /// are incomparable rather than reporting a meaningless verdict.
    /// </summary>
    /// <param name="value">The observed magnitude.</param>
    /// <param name="unit">The observation's unit.</param>
    /// <param name="range">The reference range, in its own unit.</param>
    /// <returns>Where the observation sits, or a failure when units do not reconcile.</returns>
    public static Result<ReferenceRangeVerdict> CheckRange(decimal value, string unit, ReferenceRange range)
    {
        Guard.NotNull(range, nameof(range));

        Result<decimal> converted = Convert(value, unit, range.Unit);
        if (converted.IsFailure)
        {
            // A range check that silently compares mg against mcg is worse
            // than no range check, because it looks authoritative.
            return Result.Failure<ReferenceRangeVerdict>(converted.Error!);
        }

        decimal comparable = converted.Value;

        if (comparable < range.CriticalLow || comparable > range.CriticalHigh)
        {
            return Result.Success(ReferenceRangeVerdict.Critical);
        }

        if (comparable < range.Low || comparable > range.High)
        {
            return Result.Success(ReferenceRangeVerdict.Abnormal);
        }

        return Result.Success(ReferenceRangeVerdict.Normal);
    }

    private static Result<decimal> UnknownUnit(string unit)
        => Result.Failure<decimal>(new Error(
            ErrorCodes.ValidationFailed,
            $"Unit '{unit}' is not recognised. Unknown units are refused rather than assumed.",
            ErrorCategory.Validation));
}

/// <summary>Where an observation sits relative to its reference range.</summary>
public enum ReferenceRangeVerdict
{
    /// <summary>Within the normal range.</summary>
    Normal = 0,

    /// <summary>Outside normal but not critical.</summary>
    Abnormal = 1,

    /// <summary>Outside the critical bounds; requires acknowledgement (§10.5 CriticalResultFlagged).</summary>
    Critical = 2,
}

/// <summary>A reference range with its unit and critical bounds.</summary>
public sealed class ReferenceRange
{
    /// <summary>
    /// Initializes a range.
    /// </summary>
    /// <param name="unit">The unit the bounds are expressed in.</param>
    /// <param name="low">Lower normal bound.</param>
    /// <param name="high">Upper normal bound.</param>
    /// <param name="criticalLow">Lower critical bound.</param>
    /// <param name="criticalHigh">Upper critical bound.</param>
    /// <exception cref="ArgumentException">
    /// The unit is blank, or the bounds are not ordered
    /// criticalLow ≤ low ≤ high ≤ criticalHigh.
    /// </exception>
    public ReferenceRange(string unit, decimal low, decimal high, decimal criticalLow, decimal criticalHigh)
    {
        Unit = Guard.NotNullOrWhiteSpace(unit, nameof(unit));

        if (criticalLow > low || low > high || high > criticalHigh)
        {
            throw new ArgumentException(
                "Reference bounds must be ordered criticalLow ≤ low ≤ high ≤ criticalHigh; "
                + "an inverted range silently classifies every result as normal.",
                nameof(low));
        }

        Low = low;
        High = high;
        CriticalLow = criticalLow;
        CriticalHigh = criticalHigh;
    }

    /// <summary>The unit the bounds are expressed in.</summary>
    public string Unit { get; }

    /// <summary>Lower normal bound.</summary>
    public decimal Low { get; }

    /// <summary>Upper normal bound.</summary>
    public decimal High { get; }

    /// <summary>Lower critical bound.</summary>
    public decimal CriticalLow { get; }

    /// <summary>Upper critical bound.</summary>
    public decimal CriticalHigh { get; }
}
