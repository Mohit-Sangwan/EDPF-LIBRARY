using Edpf.Abstractions.Primitives;
using Edpf.Barcode;

namespace Edpf.UnitTests.Barcode;

/// <summary>
/// Phase 17c — GS1 encoding. *"GS1 is mandatory for medication and specimen
/// traceability."*
/// </summary>
/// <remarks>
/// The expectations here come from the GS1 General Specifications' own rules
/// and published worked examples, not from what this implementation happens to
/// produce. A test asserting that the encoder agrees with itself would pass
/// against a wrong encoder.
/// </remarks>
public sealed class Gs1EncodingTests
{
    private const char Gs = Gs1ElementString.GroupSeparator;

    private static Gs1Field Field(string ai, string value)
        => new(Gs1ApplicationIdentifier.Find(ai)!, value);

    // ── check digits ───────────────────────────────────────────────────────

    [Theory]
    // GS1's published examples: payload without the check digit, expected digit.
    [InlineData("629104150021", 3)]     // GTIN-13
    [InlineData("0952003026150", 5)]    // GTIN-14
    [InlineData("40700719670720", 0)]   // SSCC fragment
    public void Mod10CheckDigit_MatchesTheGs1WorkedExamples(string payload, int expected)
    {
        Assert.Equal(expected, CheckDigit.ComputeMod10(payload).Value);
    }

    [Fact]
    public void CheckDigit_CatchesATransposition()
    {
        // The reason the scheme weights 3 and 1 alternately rather than
        // summing: transposition is the most common keying error there is, and
        // an unweighted sum would give both orderings the same digit.
        string correct = CheckDigit.Append("629104150021").Value;
        string transposed = CheckDigit.Append("629104150012").Value;

        Assert.NotEqual(correct[correct.Length - 1], transposed[transposed.Length - 1]);
    }

    [Fact]
    public void CheckDigit_WeightsFromTheRight_SoOddLengthPayloadsAreCorrect()
    {
        // A left-anchored implementation passes its GTIN-14 tests and fails on
        // GTIN-13, whose payload is odd-length. Both lengths, deliberately.
        Assert.True(CheckDigit.IsValidMod10("6291041500213"));   // 13 digits
        Assert.True(CheckDigit.IsValidMod10("09520030261505"));  // 14 digits
    }

    [Fact]
    public void CheckDigit_RejectsAnAlteredIdentifier()
    {
        Assert.False(CheckDigit.IsValidMod10("6291041500214"));
    }

    [Fact]
    public void CheckDigit_OfNonDigits_IsRefused()
    {
        Assert.True(CheckDigit.ComputeMod10("62910415002X").IsFailure);
        Assert.False(CheckDigit.IsValidMod10("ABC"));
    }

    // ── element strings ────────────────────────────────────────────────────

    [Fact]
    public void FixedLengthFieldFollowedByAnother_NeedsNoSeparator()
    {
        // (01) GTIN and (17) expiry are both fixed-length, so a scanner knows
        // where each ends without help.
        string built = Gs1ElementString.Build(
        [
            Field("01", "09520030261505"),
            Field("17", "260801"),
        ]).Value;

        Assert.Equal("010952003026150517260801", built);
        Assert.DoesNotContain(Gs, built);
    }

    [Fact]
    public void VariableLengthFieldFollowedByAnother_GetsASeparator()
    {
        // THE failure this phase exists to prevent. Without the separator a
        // scanner reads lot "ABC17260801" and finds no expiry date at all — so
        // an expired medication scans as one that never expires.
        string built = Gs1ElementString.Build(
        [
            Field("10", "ABC"),
            Field("17", "260801"),
        ]).Value;

        Assert.Equal($"10ABC{Gs}17260801", built);
    }

    [Fact]
    public void VariableLengthFieldAtTheEnd_GetsNoTrailingSeparator()
    {
        // Not wrong, but it spends a symbol character on nothing, and label
        // space on a specimen vial is genuinely scarce.
        string built = Gs1ElementString.Build(
        [
            Field("17", "260801"),
            Field("10", "LOT-9"),
        ]).Value;

        Assert.Equal("1726080110LOT-9", built);
    }

    [Fact]
    public void MedicationLabel_RoundTripsThroughParse()
    {
        // The property that makes the whole thing safe: the symbol decodes to
        // the fields that were encoded.
        Gs1Field[] original =
        [
            Field("01", "09520030261505"),
            Field("17", "260801"),
            Field("10", "LOT42"),
            Field("21", "SN-0001"),
        ];

        string built = Gs1ElementString.Build(original).Value;
        IReadOnlyList<Gs1Field> parsed = Gs1ElementString.Parse(built).Value;

        Assert.Equal(original.Length, parsed.Count);
        for (int i = 0; i < original.Length; i++)
        {
            Assert.Equal(original[i].Identifier.Ai, parsed[i].Identifier.Ai);
            Assert.Equal(original[i].Value, parsed[i].Value);
        }
    }

    [Fact]
    public void Parse_ReadsTheLongestMatchingIdentifier()
    {
        // AI "24" does not exist but "240" and "241" do. A shortest-match
        // reader takes "24" from "2401234", fails to find it, and rejects data
        // that is perfectly valid.
        IReadOnlyList<Gs1Field> parsed = Gs1ElementString.Parse("240ABC123").Value;

        Assert.Single(parsed);
        Assert.Equal("240", parsed[0].Identifier.Ai);
        Assert.Equal("ABC123", parsed[0].Value);
    }

    [Fact]
    public void SeparatorInsideAValue_IsRefused()
    {
        // The injection equivalent for barcodes: a separator inside a value
        // ends the field early and everything after it is read as a new AI.
        // Refused rather than escaped — there is no escape sequence for FNC1.
        Result<string> result = Gs1ElementString.Build([Field("10", $"AB{Gs}17991231")]);

        Assert.True(result.IsFailure);
        Assert.Contains("separator", result.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShortFixedLengthField_IsRefused()
    {
        // A five-digit expiry would absorb the first character of whatever
        // follows, shifting every field after it.
        Result<string> result = Gs1ElementString.Build([Field("17", "26080")]);

        Assert.True(result.IsFailure);
        Assert.Contains("fixed-length", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonNumericValueInANumericField_IsRefused()
    {
        Assert.True(Gs1ElementString.Build([Field("01", "0952003026150X")]).IsFailure);
    }

    [Fact]
    public void OverlongValue_IsRefused()
    {
        Assert.True(Gs1ElementString.Build([Field("10", new string('A', 21))]).IsFailure);
    }

    [Fact]
    public void UnknownIdentifier_IsRefusedRatherThanGuessedAt()
    {
        // Guessing an unknown AI's length misreads every field after it.
        Assert.Null(Gs1ApplicationIdentifier.Find("99"));

        Result<IReadOnlyList<Gs1Field>> parsed = Gs1ElementString.Parse("991234");
        Assert.True(parsed.IsFailure);
    }

    [Fact]
    public void TruncatedFixedLengthField_IsRefusedOnParse()
    {
        Assert.True(Gs1ElementString.Parse("172608").IsFailure);
    }

    // ── Code 128 ───────────────────────────────────────────────────────────

    [Fact]
    public void Gs1128_BeginsWithStartAndFnc1()
    {
        // The FNC1 in the first data position is what tells a scanner the
        // payload is an element string. Omit it and a perfectly formed GS1
        // payload arrives at the application as undifferentiated text.
        IReadOnlyList<int> symbols = Code128Encoder.EncodeGs1("17260801").Value;

        Assert.Equal(Code128Encoder.StartB, symbols[0]);
        Assert.Equal(Code128Encoder.Fnc1, symbols[1]);
        Assert.Equal(Code128Encoder.Stop, symbols[symbols.Count - 1]);
    }

    [Fact]
    public void Checksum_MatchesTheStandardsWorkedExample()
    {
        // Code 128 specification worked example: START A, then "PJJ123C".
        // Symbol values 103, 48, 42, 42, 17, 18, 19, 35 → checksum 54.
        int[] values = [103, 48, 42, 42, 17, 18, 19, 35];

        Assert.Equal(54, Code128Encoder.Checksum(values));
    }

    [Fact]
    public void Checksum_IsPositionWeighted_SoItCatchesTranspositions()
    {
        int[] original = [104, 10, 20, 30];
        int[] transposed = [104, 10, 30, 20];

        Assert.NotEqual(Code128Encoder.Checksum(original), Code128Encoder.Checksum(transposed));
    }

    [Fact]
    public void LongDigitRun_UsesSubsetC_ToHalveTheSymbolCount()
    {
        // Subset C packs two digits per symbol. On a specimen label that is
        // the difference between the barcode fitting and not.
        IReadOnlyList<int> symbols = Code128Encoder.EncodeGs1("0109520030261505").Value;

        Assert.Contains(Code128Encoder.CodeC, symbols);

        // start + fnc1 + codeC + 8 pairs + checksum + stop = 13.
        Assert.Equal(13, symbols.Count);
    }

    [Fact]
    public void SeparatorInTheData_IsEncodedAsFnc1()
    {
        IReadOnlyList<int> symbols = Code128Encoder.EncodeGs1($"10ABC{Gs}17260801").Value;

        // Two FNC1s: the leading GS1 marker, and the field separator.
        int count = 0;
        foreach (int symbol in symbols)
        {
            if (symbol == Code128Encoder.Fnc1)
            {
                count++;
            }
        }

        Assert.Equal(2, count);
    }

    [Fact]
    public void UnrepresentableCharacter_IsRefused()
    {
        Assert.True(Code128Encoder.EncodeGs1("10ABé").IsFailure);
    }

    [Fact]
    public void EmptyInput_IsRefused()
    {
        Assert.True(Code128Encoder.EncodeGs1(string.Empty).IsFailure);
        Assert.True(Gs1ElementString.Build([]).IsFailure);
    }
}
