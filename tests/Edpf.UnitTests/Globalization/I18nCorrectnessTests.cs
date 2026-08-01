using System.Globalization;
using Edpf.Abstractions.Primitives;
using Edpf.Globalization;

namespace Edpf.UnitTests.Globalization;

/// <summary>
/// Phase 27 §"Verification": the Turkish-i test, currency minor-unit tests
/// for zero-, two- and three-decimal currencies, DST boundaries, and
/// collation correctness per language.
/// </summary>
public sealed class TurkishITests
{
    private static readonly CultureInfo Turkish = new("tr-TR");

    [Fact]
    public void ToLower_UnderTurkishCulture_DemonstratesWhyItIsNeverUsedForComparison()
    {
        // The defect itself, asserted so the reason for the rule is visible:
        // uppercase I lowercases to dotless ı in Turkish, so "FILE" becomes
        // "fıle" and never equals "file".
        string lowered = "FILE".ToLower(Turkish);

        Assert.NotEqual("file", lowered);
        Assert.Equal("fıle", lowered);
    }

    [Fact]
    public void IdentifierEquals_UnderTurkishCulture_StillMatches()
    {
        // The fix: ordinal comparison, so an identifier's equality does not
        // depend on the thread's culture.
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = Turkish;

            Assert.True(TextService.IdentifierEquals("FILE", "file"));
            Assert.True(TextService.IdentifierEquals("Idempotency-Key", "idempotency-key"));
            Assert.True(TextService.IdentifierEquals("EDPF-VAL-1001", "edpf-val-1001"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void IdentifierEquals_CaseSensitiveMode_DistinguishesCase()
    {
        Assert.False(TextService.IdentifierEquals("FILE", "file", ignoreCase: false));
    }

    [Fact]
    public void ToKey_UnderTurkishCulture_ProducesTheSameKeyEverywhere()
    {
        // Culture-sensitive ToUpper would give "İ" (dotted capital) in
        // Turkey, so a key derived from user text would differ by region.
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = Turkish;
            Assert.Equal("INSULIN", TextService.ToKey("insulin"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void CompareForDisplay_Swedish_SortsAAfterZ()
    {
        // Collation genuinely differs, and a patient list sorted ordinally is
        // wrong in Swedish.
        int swedish = TextService.CompareForDisplay("ä", "z", new CultureInfo("sv-SE"));
        int german = TextService.CompareForDisplay("ä", "z", new CultureInfo("de-DE"));

        Assert.True(swedish > 0, "In Swedish, ä sorts after z.");
        Assert.True(german < 0, "In German, ä sorts with a, before z.");
    }

    [Fact]
    public void NormalizeForStorage_ComposedAndDecomposed_BecomeEqual()
    {
        // "é" as one code point vs. e + combining acute: identical on screen,
        // unequal ordinally, so a name stored one way is not found the other.
        const string composed = "é";       // é
        const string decomposed = "é";    // e + combining acute

        Assert.NotEqual(composed, decomposed);
        Assert.Equal(
            TextService.NormalizeForStorage(composed),
            TextService.NormalizeForStorage(decomposed));
    }

    [Fact]
    public void NormalizeForStorage_Null_IsEmpty()
    {
        Assert.Equal(string.Empty, TextService.NormalizeForStorage(null));
    }
}

/// <summary>Phase 27: minor-unit correctness across zero-, two- and three-decimal currencies.</summary>
public sealed class CurrencyTests
{
    [Theory]
    [InlineData("JPY", 0)]
    [InlineData("KRW", 0)]
    [InlineData("VND", 0)]
    [InlineData("USD", 2)]
    [InlineData("EUR", 2)]
    [InlineData("INR", 2)]
    [InlineData("BHD", 3)]
    [InlineData("KWD", 3)]
    [InlineData("OMR", 3)]
    public void MinorUnits_PerCurrency_IsCorrect(string code, int expected)
    {
        // Hardcoding two is a defect: it loses a factor of ten on a Kuwaiti
        // dinar and invents decimals for yen.
        Assert.Equal(expected, CurrencyService.MinorUnits(code));
    }

    [Fact]
    public void Round_Yen_HasNoDecimalPlaces()
    {
        Money rounded = CurrencyService.Round(new Money(1234.56m, "JPY"));

        Assert.Equal(1235m, rounded.Amount);
    }

    [Fact]
    public void Round_Dinar_KeepsThreeDecimalPlaces()
    {
        Money rounded = CurrencyService.Round(new Money(12.34567m, "BHD"));

        Assert.Equal(12.346m, rounded.Amount);
    }

    [Fact]
    public void Round_Always_UsesBankersRounding()
    {
        // Repeated half-up rounding introduces a systematic upward bias that
        // an auditor eventually finds.
        Assert.Equal(2m, CurrencyService.Round(new Money(2.5m, "JPY")).Amount);
        Assert.Equal(4m, CurrencyService.Round(new Money(3.5m, "JPY")).Amount);
    }

    [Fact]
    public void Add_SameCurrency_Sums()
    {
        Result<Money> sum = CurrencyService.Add(new Money(10m, "USD"), new Money(5.25m, "USD"));

        Assert.Equal(15.25m, sum.Value.Amount);
    }

    [Fact]
    public void Add_DifferentCurrencies_IsRefusedNotConverted()
    {
        // Not an addition — a question about an exchange rate on a date.
        Result<Money> sum = CurrencyService.Add(new Money(10m, "USD"), new Money(1000m, "JPY"));

        Assert.True(sum.IsFailure);
        Assert.Contains("dated rate", sum.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_Yen_ShowsNoDecimals()
    {
        string formatted = CurrencyService.Format(new Money(1234m, "JPY"), CultureInfo.InvariantCulture);

        Assert.Equal("1,234 JPY", formatted);
    }

    [Fact]
    public void Format_Dinar_ShowsThreeDecimals()
    {
        string formatted = CurrencyService.Format(new Money(12.345m, "BHD"), CultureInfo.InvariantCulture);

        Assert.Equal("12.345 BHD", formatted);
    }

    [Theory]
    [InlineData("US")]
    [InlineData("usd")]
    [InlineData("USDX")]
    [InlineData("")]
    public void Money_InvalidCurrencyCode_IsRejected(string code)
    {
        Assert.Throws<ArgumentException>(() => new Money(1m, code));
    }
}

/// <summary>
/// Phase 27 §"Time policy" and Phase 25's DST requirement: a job scheduled at
/// 02:30 must not silently run twice or vanish.
/// </summary>
public sealed class TimePolicyTests
{
    private const string London = "Europe/London";
    private const string Kolkata = "Asia/Kolkata";

    [Fact]
    public void ZonedInstant_NonUtcOffset_IsRejected()
    {
        // Store UTC, carry the zone. Baking an offset in loses which rule
        // produced it.
        Assert.Throws<ArgumentException>(
            () => new ZonedInstant(new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.FromHours(5.5)), Kolkata));
    }

    [Fact]
    public void ToLocal_HalfHourZone_IsNotTruncated()
    {
        // India is UTC+5:30; naive code truncates to +5 and shifts every
        // displayed time by half an hour.
        var instant = new ZonedInstant(new DateTimeOffset(2026, 2, 1, 6, 0, 0, TimeSpan.Zero), Kolkata);

        Result<DateTimeOffset> local = TimeZoneService.ToLocal(instant);

        Assert.True(local.IsSuccess);
        Assert.Equal(TimeSpan.FromMinutes(330), local.Value.Offset);
        Assert.Equal(11, local.Value.Hour);
        Assert.Equal(30, local.Value.Minute);
    }

    [Fact]
    public void ClassifyLocalTime_SpringForwardGap_IsReportedAsSkipped()
    {
        // 2026-03-29 01:00 UTC: UK clocks go 01:00 -> 02:00. A local 01:30
        // never happens, so a job scheduled there silently never runs.
        Result<LocalTimeKind> kind =
            TimeZoneService.ClassifyLocalTime(new DateTime(2026, 3, 29, 1, 30, 0), London);

        Assert.True(kind.IsSuccess);
        Assert.Equal(LocalTimeKind.Skipped, kind.Value);
    }

    [Fact]
    public void ClassifyLocalTime_AutumnFallBack_IsReportedAsRepeated()
    {
        // 2026-10-25: UK clocks go 02:00 -> 01:00. A local 01:30 happens
        // twice, so a job scheduled there silently runs twice.
        Result<LocalTimeKind> kind =
            TimeZoneService.ClassifyLocalTime(new DateTime(2026, 10, 25, 1, 30, 0), London);

        Assert.True(kind.IsSuccess);
        Assert.Equal(LocalTimeKind.Repeated, kind.Value);
    }

    [Fact]
    public void ClassifyLocalTime_OrdinaryTime_IsUnambiguous()
    {
        Result<LocalTimeKind> kind =
            TimeZoneService.ClassifyLocalTime(new DateTime(2026, 6, 15, 14, 0, 0), London);

        Assert.Equal(LocalTimeKind.Unambiguous, kind.Value);
    }

    [Fact]
    public void ToLocal_HistoricalDate_UsesTheRulesInForceThen()
    {
        // A summer 2015 London instant is BST (+1); a winter one is GMT (0).
        // Converting a historical record with a single stored offset would
        // shift one of them.
        var summer = new ZonedInstant(new DateTimeOffset(2015, 7, 1, 12, 0, 0, TimeSpan.Zero), London);
        var winter = new ZonedInstant(new DateTimeOffset(2015, 1, 1, 12, 0, 0, TimeSpan.Zero), London);

        Assert.Equal(TimeSpan.FromHours(1), TimeZoneService.ToLocal(summer).Value.Offset);
        Assert.Equal(TimeSpan.Zero, TimeZoneService.ToLocal(winter).Value.Offset);
    }

    [Fact]
    public void ToLocal_UnknownZone_IsRefusedNotSilentlyUtc()
    {
        // Falling back to UTC would shift every displayed time without
        // anyone noticing.
        var instant = new ZonedInstant(DateTimeOffset.UnixEpoch, "Mars/Olympus_Mons");

        Result<DateTimeOffset> local = TimeZoneService.ToLocal(instant);

        Assert.True(local.IsFailure);
        Assert.Equal(ErrorCodes.ConfigurationInvalid, local.Error!.Code);
    }
}
