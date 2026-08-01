using Edpf.Abstractions.Primitives;
using Edpf.DataPlatform.Classification;

namespace Edpf.UnitTests.DataPlatform;

/// <summary>
/// Phase 23 §"Automated PII/PHI classification": precision and recall against
/// a labelled corpus. Precision is bought with check digits — a classifier
/// that cries wolf gets muted, and a muted classifier detects nothing.
/// </summary>
public sealed class IdentifierValidatorTests
{
    [Theory]
    [InlineData("4532015112830366", true)]   // Visa test number
    [InlineData("4111111111111111", true)]   // Visa test number
    [InlineData("5500005555555559", true)]   // MasterCard test number
    [InlineData("340000000000009", true)]    // Amex test number
    [InlineData("4532015112830367", false)]  // last digit altered
    [InlineData("1234567890123456", false)]  // sequential, fails Luhn
    [InlineData("0000000000000000", true)]   // degenerate but valid
    public void IsValidLuhn_LabelledCorpus_MatchesExpectation(string candidate, bool expected)
        => Assert.Equal(expected, IdentifierValidators.IsValidLuhn(candidate));

    [Fact]
    public void IsValidLuhn_TooShortToBeACard_IsRejected()
    {
        // Short sequences match a loose digit pattern constantly; requiring
        // length is most of the precision.
        Assert.False(IdentifierValidators.IsValidLuhn("18"));
        Assert.False(IdentifierValidators.IsValidLuhn(""));
    }

    [Theory]
    [InlineData("9434765919", true)]   // NHS published example
    [InlineData("9434765918", false)]  // check digit altered
    [InlineData("943476591", false)]   // too short
    [InlineData("94347659199", false)] // too long
    public void IsValidNhsNumber_LabelledCorpus_MatchesExpectation(string candidate, bool expected)
        => Assert.Equal(expected, IdentifierValidators.IsValidNhsNumber(candidate));

    [Theory]
    [InlineData("000000000", false)] // area 000
    [InlineData("666123456", false)] // area 666
    [InlineData("900123456", false)] // area 900+
    [InlineData("123006789", false)] // group 00
    [InlineData("123450000", false)] // serial 0000
    [InlineData("123456789", true)]
    public void IsStructurallyValidSsn_ReservedRanges_AreRejected(string candidate, bool expected)
        => Assert.Equal(expected, IdentifierValidators.IsStructurallyValidSsn(candidate));

    [Theory]
    [InlineData("012345678901", false)] // first digit 0
    [InlineData("112345678901", false)] // first digit 1
    [InlineData("23456789012", false)]  // too short
    public void IsValidAadhaar_StructuralRules_AreEnforced(string candidate, bool expected)
        => Assert.Equal(expected, IdentifierValidators.IsValidAadhaar(candidate));

    [Fact]
    public void DigitsOnly_FormattedIdentifier_IsNormalisedForValidation()
    {
        // Real data arrives formatted; validating the raw string would miss
        // every hyphenated card number.
        Assert.Equal("4532015112830366", IdentifierValidators.DigitsOnly("4532-0151-1283-0366"));
        Assert.Equal("123456789", IdentifierValidators.DigitsOnly("123-45-6789"));
    }
}

/// <summary>
/// The classifier and, more importantly, the **classification drift**
/// detector — the thing that catches the developer who added an unmarked
/// PHI column.
/// </summary>
public sealed class DataClassifierTests
{
    [Fact]
    public void Classify_ValidCard_IsHighConfidencePci()
    {
        ClassificationFinding? finding = DataClassifier.Classify("PaymentToken", "4532-0151-1283-0366");

        Assert.NotNull(finding);
        Assert.Equal(SensitiveDataKind.PaymentCard, finding.DetectedKind);
        Assert.Equal(DataClassificationLevel.Pci, finding.SuggestedLevel);
        Assert.True(finding.IsHighConfidence);
    }

    [Fact]
    public void Classify_SixteenDigitOrderNumber_IsNotReportedAsACard()
    {
        // The precision case. Without Luhn this fires on every long numeric
        // field in the estate, and the team stops reading the report.
        ClassificationFinding? finding = DataClassifier.Classify("OrderNumber", "1234567890123456");

        Assert.Null(finding);
    }

    [Fact]
    public void Classify_ValidNhsNumber_IsHighConfidencePhi()
    {
        ClassificationFinding? finding = DataClassifier.Classify("PatientRef", "943 476 5919");

        Assert.NotNull(finding);
        Assert.Equal(SensitiveDataKind.NhsNumber, finding.DetectedKind);
        Assert.Equal(DataClassificationLevel.Phi, finding.SuggestedLevel);
    }

    [Fact]
    public void Classify_Ssn_IsReportedAsPii()
    {
        ClassificationFinding? finding = DataClassifier.Classify("TaxId", "123-45-6789");

        Assert.NotNull(finding);
        Assert.Equal(SensitiveDataKind.SocialSecurityNumber, finding.DetectedKind);
    }

    [Fact]
    public void Classify_Email_IsReportedButLowConfidence()
    {
        // No check digit exists for an email, so it is reported for review
        // rather than treated as merge-blocking.
        ClassificationFinding? finding = DataClassifier.Classify("ContactField", "asha.verma@example.com");

        Assert.NotNull(finding);
        Assert.Equal(SensitiveDataKind.EmailAddress, finding.DetectedKind);
        Assert.False(finding.IsHighConfidence);
    }

    [Fact]
    public void Classify_OrdinaryProse_ReportsNothing()
    {
        Assert.Null(DataClassifier.Classify("Notes", "Patient reports improvement since last visit."));
        Assert.Null(DataClassifier.Classify("Notes", ""));
        Assert.Null(DataClassifier.Classify("Notes", null));
    }

    [Fact]
    public void Classify_Finding_CarriesNoValue()
    {
        // A classification report is itself a document that gets emailed
        // around; it must not become the leak it was written to prevent.
        ClassificationFinding? finding = DataClassifier.Classify("TaxId", "123-45-6789");

        Assert.DoesNotContain("123-45-6789", finding!.ToString(), StringComparison.Ordinal);
    }

    // ── the drift detector ─────────────────────────────────────────────────

    [Fact]
    public void DetectDrift_UnmarkedPhiColumn_IsReported()
    {
        // The scenario the phase names: a developer adds PatientNotes and
        // forgets the attribute, silently opting the column out of
        // encryption, redaction, audit and export control.
        var samples = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["PatientNotes"] = ["Contact patient on 943 476 5919 regarding results"],
        };
        var declared = new Dictionary<string, DataClassificationLevel>(StringComparer.Ordinal)
        {
            ["PatientNotes"] = DataClassificationLevel.Internal,
        };

        IReadOnlyList<ClassificationFinding> drift = DataClassifier.DetectDrift(samples, declared);

        ClassificationFinding finding = Assert.Single(drift);
        Assert.Equal("PatientNotes", finding.FieldName);
        Assert.Equal(DataClassificationLevel.Phi, finding.SuggestedLevel);
    }

    [Fact]
    public void DetectDrift_CorrectlyClassifiedColumn_IsNotReported()
    {
        var samples = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["NhsNumber"] = ["943 476 5919"],
        };
        var declared = new Dictionary<string, DataClassificationLevel>(StringComparer.Ordinal)
        {
            ["NhsNumber"] = DataClassificationLevel.Phi,
        };

        Assert.Empty(DataClassifier.DetectDrift(samples, declared));
    }

    [Fact]
    public void DetectDrift_OverClassifiedColumn_IsNotReported()
    {
        // Tightening a classification must never produce noise, or teams
        // stop tightening.
        var samples = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["Email"] = ["asha@example.com"],
        };
        var declared = new Dictionary<string, DataClassificationLevel>(StringComparer.Ordinal)
        {
            ["Email"] = DataClassificationLevel.Phi,
        };

        Assert.Empty(DataClassifier.DetectDrift(samples, declared));
    }

    [Fact]
    public void DetectDrift_UndeclaredColumn_DefaultsToPublicAndIsReported()
    {
        // A column nobody classified at all is the worst case, and is
        // treated as Public so any sensitive content shows as drift.
        var samples = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["MysteryColumn"] = ["4532-0151-1283-0366"],
        };

        IReadOnlyList<ClassificationFinding> drift = DataClassifier.DetectDrift(
            samples, new Dictionary<string, DataClassificationLevel>(StringComparer.Ordinal));

        Assert.Single(drift);
    }

    [Fact]
    public void DetectDrift_ManySamplesOneField_ReportsOnceNotPerRow()
    {
        // A drift report with ten thousand identical findings is a report
        // nobody reads.
        var samples = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["Notes"] = Enumerable.Repeat("943 476 5919", 500).ToList(),
        };

        IReadOnlyList<ClassificationFinding> drift = DataClassifier.DetectDrift(
            samples, new Dictionary<string, DataClassificationLevel>(StringComparer.Ordinal));

        Assert.Single(drift);
    }
}
