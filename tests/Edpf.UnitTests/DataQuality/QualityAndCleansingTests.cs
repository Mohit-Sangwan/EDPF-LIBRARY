using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;
using Edpf.DataQuality;
using Edpf.Metadata;
using Edpf.UnitTests.TestDoubles;

namespace Edpf.UnitTests.DataQuality;

/// <summary>
/// Phase 23d — quality scoring, gates, and audited cleansing.
/// </summary>
public sealed class QualityAndCleansingTests
{
    private static readonly DateTimeOffset Assessed = new(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);

    private static FieldMetadata Field(
        string name, DataClassificationLevel classification = DataClassificationLevel.Internal)
        => new FieldMetadata(
            name, name, typeof(string), classification,
            isFilterable: classification < DataClassificationLevel.Confidential);

    // ── scoring ────────────────────────────────────────────────────────────

    [Fact]
    public void DimensionAssessedOverZeroRows_ScoresZero_NotOne()
    {
        // An empty dataset is not a perfect one. Scoring it perfect is how a
        // broken import passes a quality gate.
        var score = new DimensionScore(QualityDimension.Completeness, total: 0, passed: 0, "non-empty");

        Assert.Equal(0m, score.Score);
    }

    [Fact]
    public void UnassessedDimension_IsDistinctFromScoringZero()
    {
        // Opposite facts. Collapsing them hides which one holds.
        var score = new QualityScore("import", Assessed,
            [new DimensionScore(QualityDimension.Completeness, 100, 100, "non-empty")]);

        Assert.True(score.For(QualityDimension.Completeness).IsSuccess);

        Result<DimensionScore> timeliness = score.For(QualityDimension.Timeliness);
        Assert.True(timeliness.IsFailure);
        Assert.Contains("not the same as scoring zero", timeliness.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WeakestScore_IsTheMinimum_NotTheAverage()
    {
        // Averaging lets a dataset that is perfectly complete and entirely
        // invalid look acceptable.
        var score = new QualityScore("import", Assessed,
        [
            new DimensionScore(QualityDimension.Completeness, 100, 100, "non-empty"),
            new DimensionScore(QualityDimension.Validity, 100, 10, "matches declared pattern"),
        ]);

        Assert.Equal(0.1m, score.WeakestScore);
    }

    [Fact]
    public void DimensionScore_CarriesHowItWasMeasured()
    {
        // "Accuracy 94%" means nothing. "94% matched the national register"
        // means something, and so does "94% were non-empty" — a much weaker
        // claim wearing the same label.
        var score = new DimensionScore(
            QualityDimension.Accuracy, 100, 94, "matched the national register");

        Assert.Equal("matched the national register", score.Method);
    }

    [Fact]
    public void ScoringADimensionTwice_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => new QualityScore("import", Assessed,
        [
            new DimensionScore(QualityDimension.Validity, 100, 90, "a"),
            new DimensionScore(QualityDimension.Validity, 100, 10, "b"),
        ]));
    }

    [Fact]
    public void MorePassingThanAssessed_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DimensionScore(QualityDimension.Validity, total: 10, passed: 11, "x"));
    }

    // ── gates ──────────────────────────────────────────────────────────────

    [Fact]
    public void BelowThresholdData_IsQuarantined_NotRejectedAndNotIngested()
    {
        // Rejecting loses the data and the sender finds out too late.
        // Ingesting with a warning is worse: the bad data becomes
        // indistinguishable from the good and every consumer inherits it.
        var gate = new QualityGate("import").Require(QualityDimension.Completeness, 0.95m);

        var score = new QualityScore("import", Assessed,
            [new DimensionScore(QualityDimension.Completeness, 100, 60, "non-empty")]);

        GateResult result = gate.Evaluate(score);

        Assert.Equal(GateDecision.Quarantine, result.Decision);
        Assert.False(result.Admitted);
        Assert.Single(result.Failures);
    }

    [Fact]
    public void GateFailure_SaysWhichDimensionAndByHowMuch()
    {
        var gate = new QualityGate("import").Require(QualityDimension.Validity, 0.90m);

        var score = new QualityScore("import", Assessed,
            [new DimensionScore(QualityDimension.Validity, 100, 42, "matches declared pattern")]);

        string failure = gate.Evaluate(score).Failures[0];

        Assert.Contains("Validity", failure, StringComparison.Ordinal);
        Assert.Contains("matches declared pattern", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredButUnassessedDimension_FailsTheGate()
    {
        // Treating an unmeasured dimension as satisfied would let a gate be
        // bypassed by simply not running the check — the easiest bypass there
        // is, and the one that looks like an accident.
        var gate = new QualityGate("import")
            .Require(QualityDimension.Completeness, 0.9m)
            .Require(QualityDimension.Uniqueness, 0.9m);

        var score = new QualityScore("import", Assessed,
            [new DimensionScore(QualityDimension.Completeness, 100, 100, "non-empty")]);

        GateResult result = gate.Evaluate(score);

        Assert.Equal(GateDecision.Quarantine, result.Decision);
        Assert.Contains("not assessed", result.Failures[0], StringComparison.Ordinal);
    }

    [Fact]
    public void MeetingEveryThreshold_Admits()
    {
        var gate = new QualityGate("import")
            .Require(QualityDimension.Completeness, 0.9m)
            .Require(QualityDimension.Validity, 0.8m);

        var score = new QualityScore("import", Assessed,
        [
            new DimensionScore(QualityDimension.Completeness, 100, 95, "non-empty"),
            new DimensionScore(QualityDimension.Validity, 100, 85, "matches pattern"),
        ]);

        Assert.True(gate.Evaluate(score).Admitted);
    }

    [Fact]
    public void ThresholdOutsideZeroToOne_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new QualityGate("g").Require(QualityDimension.Validity, 1.5m));
    }

    // ── cleansing ──────────────────────────────────────────────────────────

    [Fact]
    public void EveryChange_RecordsWhatTheValueWasBefore()
    {
        // "Cleansing clinical data is a change to the medical record and must
        // be traceable." Standardising a name looks like housekeeping; it is
        // an amendment made by a process rather than a person.
        var clock = new FakeClock();
        var cleaner = new DataCleaner(clock);
        var rule = new CleansingRule("TrimWhitespace", "FamilyName", v => v?.Trim());

        cleaner.Apply(rule, Field("FamilyName", DataClassificationLevel.Pii), "row-1", "  Okonkwo  ");

        CleansingRecord record = Assert.Single(cleaner.Trail);
        Assert.Equal("  Okonkwo  ", record.Before);
        Assert.Equal("Okonkwo", record.After);
        Assert.Equal("TrimWhitespace", record.RuleName);
        Assert.Equal("row-1", record.RowKey);
    }

    [Fact]
    public void TrailHoldsTheBeforeValueUnredacted_EvenForClassifiedFields()
    {
        // A trail that redacts the before-value cannot reverse the change,
        // which is the reason it exists. It inherits the field's
        // classification instead, so it lands in equally protected storage.
        var cleaner = new DataCleaner(new FakeClock());
        var rule = new CleansingRule("Normalize", "RecordNumber", v => v?.ToUpperInvariant());

        cleaner.Apply(rule, Field("RecordNumber", DataClassificationLevel.Phi), "row-1", "mrn-1");

        CleansingRecord record = Assert.Single(cleaner.Trail);
        Assert.Equal("mrn-1", record.Before);
        Assert.Equal(DataClassificationLevel.Phi, record.Classification);
    }

    [Fact]
    public void UnchangedValue_RecordsNothing()
    {
        // A trail padded with no-ops is a trail nobody reads, and the changes
        // that matter get lost in it.
        var cleaner = new DataCleaner(new FakeClock());
        var rule = new CleansingRule("TrimWhitespace", "FamilyName", v => v?.Trim());

        cleaner.Apply(rule, Field("FamilyName"), "row-1", "Okonkwo");

        Assert.Empty(cleaner.Trail);
    }

    [Fact]
    public void OriginalValue_ReturnsTheValueAsItArrived_NotAnIntermediateState()
    {
        // Reversing to an intermediate state would restore a value that was
        // itself the output of a rule.
        var cleaner = new DataCleaner(new FakeClock());
        IFieldMetadata field = Field("FamilyName");

        cleaner.Apply(new CleansingRule("Trim", "FamilyName", v => v?.Trim()), field, "row-1", " o'brien ");
        cleaner.Apply(
            new CleansingRule("Case", "FamilyName", v => v?.ToUpperInvariant()), field, "row-1", "o'brien");

        Assert.Equal(" o'brien ", cleaner.OriginalValue("row-1", "FamilyName").Value);
    }

    [Fact]
    public void OriginalValue_ForAnUntouchedRow_IsNotFound()
    {
        var cleaner = new DataCleaner(new FakeClock());

        Assert.True(cleaner.OriginalValue("row-99", "FamilyName").IsFailure);
    }

    [Fact]
    public void RuleAppliedToTheWrongField_IsRefused()
    {
        var cleaner = new DataCleaner(new FakeClock());
        var rule = new CleansingRule("Trim", "FamilyName", v => v?.Trim());

        Result<string?> result = cleaner.Apply(rule, Field("GivenName"), "row-1", " x ");

        Assert.True(result.IsFailure);
        Assert.Empty(cleaner.Trail);
    }

    [Fact]
    public void RuleThatThrows_LeavesTheValueUnchangedAndNamesTheRule()
    {
        // A rule that throws must not take the import down, and must not leave
        // the value half-changed.
        var cleaner = new DataCleaner(new FakeClock());
        var rule = new CleansingRule(
            "Broken", "FamilyName", _ => throw new InvalidOperationException("boom"));

        Result<string?> result = cleaner.Apply(rule, Field("FamilyName"), "row-1", "value");

        Assert.True(result.IsFailure);
        Assert.Contains("Broken", result.Error!.Message, StringComparison.Ordinal);
        Assert.Empty(cleaner.Trail);
    }

    // ── similarity ─────────────────────────────────────────────────────────

    [Theory]
    // Published examples from the Jaro-Winkler literature.
    [InlineData("MARTHA", "MARHTA", 0.961)]
    [InlineData("DIXON", "DICKSONX", 0.813)]
    [InlineData("DWAYNE", "DUANE", 0.840)]
    public void JaroWinkler_MatchesThePublishedExamples(string a, string b, double expected)
    {
        decimal actual = StringSimilarity.JaroWinkler(a, b);

        Assert.InRange((double)actual, expected - 0.001, expected + 0.001);
    }

    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("flaw", "lawn", 2)]
    [InlineData("", "abc", 3)]
    [InlineData("same", "same", 0)]
    public void Levenshtein_MatchesTheKnownDistances(string a, string b, int expected)
    {
        Assert.Equal(expected, StringSimilarity.Levenshtein(a, b));
    }

    [Fact]
    public void Similarity_IsSymmetric()
    {
        Assert.Equal(
            StringSimilarity.JaroWinkler("MARTHA", "MARHTA"),
            StringSimilarity.JaroWinkler("MARHTA", "MARTHA"));
        Assert.Equal(
            StringSimilarity.Levenshtein("kitten", "sitting"),
            StringSimilarity.Levenshtein("sitting", "kitten"));
    }

    [Fact]
    public void Similarity_IsOrdinal_NotCultureSensitive()
    {
        // A similarity that varied with the server's culture would make the
        // same pair of names match in one region and not another (Phase 27).
        Assert.NotEqual(1m, StringSimilarity.JaroWinkler("ISTANBUL", "ıstanbul"));
    }

    [Fact]
    public void IdenticalStrings_ScoreOne_AndDisjointStringsScoreZero()
    {
        Assert.Equal(1m, StringSimilarity.JaroWinkler("Okonkwo", "Okonkwo"));
        Assert.Equal(0m, StringSimilarity.Jaro("abc", "xyz"));
        Assert.Equal(1m, StringSimilarity.EditSimilarity(string.Empty, string.Empty));
    }

    [Fact]
    public void JaroWinkler_ScalingFactorAboveTheSafeRange_IsRefused()
    {
        // Above 0.25 the prefix boost can exceed the headroom and push the
        // result past 1, turning a similarity into a nonsense number.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StringSimilarity.JaroWinkler("a", "b", 0.5m));
    }

    [Fact]
    public void JaroWinkler_NeverExceedsOne()
    {
        Assert.True(StringSimilarity.JaroWinkler("ABCD", "ABCE") <= 1m);
        Assert.True(StringSimilarity.JaroWinkler("ABCD", "ABCD", 0.25m) <= 1m);
    }
}
