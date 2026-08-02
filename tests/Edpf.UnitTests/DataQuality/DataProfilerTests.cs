using Edpf.Abstractions.Metadata;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;
using Edpf.DataQuality;
using Edpf.Metadata;

namespace Edpf.UnitTests.DataQuality;

/// <summary>
/// Phase 23d — profiling, and the disclosure route it would otherwise open.
/// </summary>
public sealed class DataProfilerTests
{
    private static FieldMetadata Field(
        string name, DataClassificationLevel classification = DataClassificationLevel.Internal)
        => new FieldMetadata(
            name, name, typeof(string), classification,
            isFilterable: classification < DataClassificationLevel.Confidential);

    private static List<string?> Repeat(string value, int times)
    {
        var values = new List<string?>(times);
        for (int i = 0; i < times; i++)
        {
            values.Add(value);
        }

        return values;
    }

    // ── the disclosure route ───────────────────────────────────────────────

    [Fact]
    public void ProfileOfAClassifiedColumn_DisclosesNoValues()
    {
        // A "ten most common values" report over a medical record number
        // column IS the medical record numbers. The report is not metadata
        // about the data — for a classified column it is a projection of it.
        var profiler = new DataProfiler(minimumCellSize: 1);
        List<string?> values = Repeat("MRN-000123", 8);

        ColumnProfile profile = profiler.Profile(
            Field("RecordNumber", DataClassificationLevel.Phi), values);

        Assert.True(profile.ValuesWithheld);
        foreach (ValueFrequency frequency in profile.TopValues)
        {
            Assert.Equal(DataProfiler.WithheldMarker, frequency.Value);
        }

        Assert.DoesNotContain("MRN-000123", string.Join("|", profile.TopValues.Select(v => v.Value).ToList()),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileOfAClassifiedColumn_StillReportsAggregateStatistics()
    {
        // Withholding everything would make the profiler useless on exactly
        // the columns a data steward most needs to assess. Null rate and
        // cardinality describe the column's shape and disclose nothing about
        // any individual.
        var profiler = new DataProfiler(minimumCellSize: 1);
        List<string?> values = [.. Repeat("A", 6), null, null];

        ColumnProfile profile = profiler.Profile(
            Field("RecordNumber", DataClassificationLevel.Phi), values);

        Assert.Equal(8, profile.RowCount);
        Assert.Equal(2, profile.NullCount);
        Assert.Equal(0.25m, profile.NullRate);
        Assert.Equal(1, profile.DistinctCount);
    }

    [Fact]
    public void ProfileOfAClassifiedColumn_InfersNoPattern()
    {
        // A pattern derived tightly from a classified column is itself a
        // disclosure: a "pattern" matching exactly one value is that value.
        var profiler = new DataProfiler(minimumCellSize: 1);

        ColumnProfile profile = profiler.Profile(
            Field("NationalId", DataClassificationLevel.Phi), Repeat("AB123456C", 10));

        Assert.Null(profile.InferredPattern);
    }

    [Fact]
    public void RareValue_IsSuppressed_EvenInAnUnclassifiedColumn()
    {
        // Small-cell suppression. In a cohort of 400, "one patient has this
        // postcode" identifies that patient even though a postcode alone is
        // not PHI.
        var profiler = new DataProfiler(minimumCellSize: 5);
        List<string?> values = [.. Repeat("common", 20), "unique-outlier"];

        ColumnProfile profile = profiler.Profile(Field("Postcode"), values);

        Assert.DoesNotContain(
            profile.TopValues, v => v.Value == "unique-outlier");
        Assert.Contains(profile.TopValues, v => v.Value == "common");
        Assert.True(profile.ValuesWithheld);
    }

    [Fact]
    public void WithholdingIsRecorded_NotSilent()
    {
        // A reader seeing an empty TopValues would conclude the column is
        // empty. The flag tells them the profile is complete and the values
        // are not theirs to see.
        var profiler = new DataProfiler(minimumCellSize: 5);

        ColumnProfile profile = profiler.Profile(Field("Rare"), ["a", "b", "c"]);

        Assert.Empty(profile.TopValues);
        Assert.True(profile.ValuesWithheld);
        Assert.Equal(3, profile.RowCount);
    }

    [Fact]
    public void UnclassifiedColumnWithCommonValues_ReportsThemNormally()
    {
        var profiler = new DataProfiler(minimumCellSize: 2);
        List<string?> values = [.. Repeat("Ward-A", 5), .. Repeat("Ward-B", 3)];

        ColumnProfile profile = profiler.Profile(Field("Location", DataClassificationLevel.Public), values);

        Assert.False(profile.ValuesWithheld);
        Assert.Equal("Ward-A", profile.TopValues[0].Value);
        Assert.Equal(5, profile.TopValues[0].Count);
        Assert.Equal("Ward-B", profile.TopValues[1].Value);
    }

    // ── statistics ─────────────────────────────────────────────────────────

    [Fact]
    public void NullRate_CountsEmptyStringsAsAbsent()
    {
        // An empty string in an imported CSV is an absent value wearing a
        // disguise. Counting it as present would report a column as complete
        // when it is not, which is the completeness dimension lying.
        var profiler = new DataProfiler(minimumCellSize: 1);

        ColumnProfile profile = profiler.Profile(Field("Given"), ["a", "", null, "b"]);

        Assert.Equal(2, profile.NullCount);
        Assert.Equal(0.5m, profile.NullRate);
    }

    [Fact]
    public void Cardinality_IsOverPopulatedRows_NotAllRows()
    {
        // Otherwise a mostly-null column looks low-cardinality, and a steward
        // reads that as "a category" when it is actually "barely filled in".
        var profiler = new DataProfiler(minimumCellSize: 1);

        ColumnProfile profile = profiler.Profile(Field("Code"), ["x", "y", null, null]);

        Assert.Equal(1m, profile.Cardinality);
    }

    [Fact]
    public void EmptyColumn_ScoresZeroLengthsWithoutFailing()
    {
        var profiler = new DataProfiler(minimumCellSize: 1);

        ColumnProfile profile = profiler.Profile(Field("Empty"), [null, null]);

        Assert.Equal(0, profile.MinLength);
        Assert.Equal(0, profile.MaxLength);
        Assert.Equal(0m, profile.Cardinality);
    }

    [Fact]
    public void TopValues_AreOrderedStably_SoProfilesCanBeDiffed()
    {
        // A profile that shuffles between runs is one nobody can compare with
        // last week's, which is what trend tracking requires.
        var profiler = new DataProfiler(minimumCellSize: 1);
        List<string?> values = [.. Repeat("b", 3), .. Repeat("a", 3), .. Repeat("c", 3)];

        ColumnProfile first = profiler.Profile(Field("Code"), values);
        ColumnProfile second = profiler.Profile(Field("Code"), values);

        Assert.Equal(
            first.TopValues.Select(v => v.Value).ToList(),
            second.TopValues.Select(v => v.Value).ToList());
        Assert.Equal("a", first.TopValues[0].Value);
    }

    // ── pattern inference ──────────────────────────────────────────────────

    [Fact]
    public void ConsistentValues_InferACoarseShape()
    {
        var profiler = new DataProfiler(minimumCellSize: 1);

        ColumnProfile profile = profiler.Profile(
            Field("Reference", DataClassificationLevel.Public), ["AB-1234", "CD-5678"]);

        Assert.Equal("AA-9999", profile.InferredPattern);
    }

    [Fact]
    public void DisagreeingValues_InferNoPattern()
    {
        // Reporting the most common shape would invite a validation rule that
        // rejects the legitimate minority.
        var profiler = new DataProfiler(minimumCellSize: 1);

        ColumnProfile profile = profiler.Profile(
            Field("Reference", DataClassificationLevel.Public), ["AB-1234", "LONGER-VALUE-1"]);

        Assert.Null(profile.InferredPattern);
    }

    [Fact]
    public void Profiler_AgreesWithTheRedactorAboutWhatIsSensitive()
    {
        // The profiler asks ProtectionPolicy, the same table every other
        // subsystem asks (ADR-025). A profiler with its own opinion would
        // drift from the redactor's, and the gap would be a disclosure.
        var profiler = new DataProfiler(minimumCellSize: 1);

        foreach (DataClassificationLevel level in Enum.GetValues<DataClassificationLevel>())
        {
            bool policySaysRedact = ProtectionPolicy.Default
                .For(level)
                .HasFlagSet(DataProtectionRequirements.RedactInDiagnostics);

            ColumnProfile profile = profiler.Profile(Field("F", level), Repeat("value", 10));

            Assert.Equal(policySaysRedact, profile.ValuesWithheld);
        }
    }
}
