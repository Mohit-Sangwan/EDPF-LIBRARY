using Edpf.Abstractions.Primitives;
using Edpf.DataPlatform.Clinical;

namespace Edpf.UnitTests.DataPlatform;

/// <summary>
/// Phase 24 §"Clinical safety": unit-conversion validation — the class of
/// error that kills patients. A dose of 5 mg administered as 5 mcg is a
/// thousand-fold error, and both look plausible on a screen.
/// </summary>
public sealed class UnitConversionSafetyTests
{
    [Theory]
    [InlineData(5, "mg", "mcg", 5_000)]
    [InlineData(5, "mcg", "mg", 0.005)]
    [InlineData(1, "g", "mg", 1_000)]
    [InlineData(1, "kg", "g", 1_000)]
    [InlineData(2.5, "mg", "ng", 2_500_000)]
    public void Convert_MassUnits_IsExact(decimal value, string from, string to, decimal expected)
    {
        Result<decimal> result = UnitConverter.Convert(value, from, to);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public void Convert_MgToMcg_IsExactlyOneThousandFold()
    {
        // The specific error this exists to prevent, asserted directly.
        Result<decimal> result = UnitConverter.Convert(1m, "mg", "mcg");

        Assert.Equal(1_000m, result.Value);
    }

    [Fact]
    public void Convert_UgAndMcg_AreTheSameUnit()
    {
        // Both spellings appear in real orders; treating them as different
        // units would be its own error class.
        Assert.Equal(
            UnitConverter.Convert(1m, "ug", "mg").Value,
            UnitConverter.Convert(1m, "mcg", "mg").Value);
    }

    [Fact]
    public void Convert_IsCaseSensitive_BecauseUcumIs()
    {
        // "mg" and "Mg" differ by nine orders of magnitude. Accepting either
        // case is how a megagram becomes a milligram.
        Result<decimal> result = UnitConverter.Convert(1m, "MG", "mcg");

        Assert.True(result.IsFailure);
    }

    [Theory]
    [InlineData("mg", "mL")]
    [InlineData("g", "L")]
    [InlineData("mmol", "mg")]
    [InlineData("mg", "h")]
    public void Convert_AcrossDimensions_IsRefusedNotCoerced(string from, string to)
    {
        // Mass to volume needs a density; substance to mass needs a molar
        // mass. Both are questions about a substance, and a unit converter
        // must not answer them.
        Result<decimal> result = UnitConverter.Convert(1m, from, to);

        Assert.True(result.IsFailure);
        Assert.Contains("depends on the substance", result.Error!.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("milligrams")]
    [InlineData("mgs")]
    [InlineData("units")]
    [InlineData("iu")]
    public void Convert_UnknownUnit_IsRefusedNotAssumed(string unknown)
    {
        // Assuming is how a typo becomes a dose.
        Result<decimal> result = UnitConverter.Convert(1m, unknown, "mg");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.ValidationFailed, result.Error!.Code);
    }

    [Fact]
    public void Convert_RoundTrip_LosesNoPrecision()
    {
        // decimal, not double: 0.1 has no exact binary representation, and a
        // rounding artefact in a dose calculation is not acceptable.
        Result<decimal> toMcg = UnitConverter.Convert(0.1m, "mg", "mcg");
        Result<decimal> backToMg = UnitConverter.Convert(toMcg.Value, "mcg", "mg");

        Assert.Equal(0.1m, backToMg.Value);
    }

    [Theory]
    [InlineData(60, "s", "min", 1)]
    [InlineData(24, "h", "d", 1)]
    [InlineData(1, "d", "h", 24)]
    public void Convert_TimeUnits_AreExact(decimal value, string from, string to, decimal expected)
        => Assert.Equal(expected, UnitConverter.Convert(value, from, to).Value);

    [Fact]
    public void KnownUnits_AreEnumerable_SoCoverageIsCheckable()
    {
        Assert.Contains("mg", UnitConverter.KnownUnits);
        Assert.Contains("mcg", UnitConverter.KnownUnits);
        Assert.Contains("mmol", UnitConverter.KnownUnits);
    }
}

/// <summary>Phase 24: reference-range checking that reconciles units first.</summary>
public sealed class ReferenceRangeTests
{
    // Serum potassium, a classic critical-result analyte.
    private static ReferenceRange Potassium =>
        new("mmol", low: 3.5m, high: 5.0m, criticalLow: 2.5m, criticalHigh: 6.5m);

    [Theory]
    [InlineData(4.2, ReferenceRangeVerdict.Normal)]
    [InlineData(3.5, ReferenceRangeVerdict.Normal)]
    [InlineData(5.0, ReferenceRangeVerdict.Normal)]
    [InlineData(5.6, ReferenceRangeVerdict.Abnormal)]
    [InlineData(3.0, ReferenceRangeVerdict.Abnormal)]
    [InlineData(6.9, ReferenceRangeVerdict.Critical)]
    [InlineData(2.1, ReferenceRangeVerdict.Critical)]
    public void CheckRange_Always_ClassifiesAgainstTheBounds(decimal value, ReferenceRangeVerdict expected)
    {
        Result<ReferenceRangeVerdict> result = UnitConverter.CheckRange(value, "mmol", Potassium);

        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public void CheckRange_ConvertibleUnit_IsReconciledBeforeComparing()
    {
        // 4200 umol is 4.2 mmol — normal. Comparing the raw magnitudes would
        // report a wildly critical result.
        Result<ReferenceRangeVerdict> result = UnitConverter.CheckRange(4_200m, "umol", Potassium);

        Assert.Equal(ReferenceRangeVerdict.Normal, result.Value);
    }

    [Fact]
    public void CheckRange_IncomparableUnit_IsRefusedNotGuessed()
    {
        // A range check that silently compares mg against mmol is worse than
        // no range check, because it looks authoritative.
        Result<ReferenceRangeVerdict> result = UnitConverter.CheckRange(4.2m, "mg", Potassium);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void ReferenceRange_InvertedBounds_AreRejectedAtConstruction()
    {
        // An inverted range silently classifies every result as normal —
        // a defect that would hide every critical value it touched.
        Assert.Throws<ArgumentException>(
            () => new ReferenceRange("mmol", low: 5.0m, high: 3.5m, criticalLow: 2.5m, criticalHigh: 6.5m));

        Assert.Throws<ArgumentException>(
            () => new ReferenceRange("mmol", low: 3.5m, high: 5.0m, criticalLow: 4.0m, criticalHigh: 6.5m));
    }

    [Fact]
    public void ReferenceRange_BlankUnit_IsRejected()
    {
        Assert.Throws<ArgumentException>(
            () => new ReferenceRange("  ", 1m, 2m, 0m, 3m));
    }
}
