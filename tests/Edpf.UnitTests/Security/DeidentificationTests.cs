using System.Text;
using Edpf.Abstractions.Security;
using Edpf.Security;

namespace Edpf.UnitTests.Security;

/// <summary>
/// Phase 20: de-identification validated against a **re-identification
/// attack attempt**, not a round-trip test. The output is meant to be
/// readable; its safety rests on the absence of identifiers.
/// </summary>
public sealed class DeidentificationTests
{
    private static SafeHarborDeidentifier Deidentifier =>
        new(Encoding.UTF8.GetBytes("test-shift-salt-not-a-production-secret"));

    private static Dictionary<string, object?> ClinicalRecord() => new(StringComparer.Ordinal)
    {
        ["SubjectToken"] = "tok-abc123",
        ["FamilyName"] = "Rutherford",
        ["MedicalRecordNumber"] = "MRN-99887766",
        ["SocialSecurityNumber"] = "123-45-6789",
        ["Email"] = "asha.verma@example.com",
        ["Phone"] = "+91-98765-43210",
        ["PostCode"] = "560034",
        ["DateOfBirth"] = new DateTime(1984, 3, 14),
        ["AdmissionDate"] = new DateTime(2026, 2, 1),
        ["Diagnosis"] = "Type 2 diabetes mellitus",
        ["SystolicBp"] = 138,
    };

    private static SafeHarborPolicy Policy(bool rejectUnmapped = true) => new(
        new Dictionary<string, SafeHarborIdentifier>(StringComparer.Ordinal)
        {
            ["FamilyName"] = SafeHarborIdentifier.Name,
            ["MedicalRecordNumber"] = SafeHarborIdentifier.MedicalRecordNumber,
            ["SocialSecurityNumber"] = SafeHarborIdentifier.SocialSecurityNumber,
            ["Email"] = SafeHarborIdentifier.EmailAddress,
            ["Phone"] = SafeHarborIdentifier.TelephoneNumber,
            ["PostCode"] = SafeHarborIdentifier.GeographicSubdivision,
            ["DateOfBirth"] = SafeHarborIdentifier.DateElement,
            ["AdmissionDate"] = SafeHarborIdentifier.DateElement,

            // Explicitly classified as carrying no identifier.
            ["Diagnosis"] = SafeHarborIdentifier.None,
            ["SystolicBp"] = SafeHarborIdentifier.None,
        },
        subjectTokenField: "SubjectToken",
        rejectUnmappedFields: rejectUnmapped);

    // ── the re-identification attempt ──────────────────────────────────────

    [Fact]
    public void SafeHarbor_Always_RemovesEveryDirectIdentifier()
    {
        DeidentificationResult result = Deidentifier.ApplySafeHarbor(ClinicalRecord(), Policy());

        string rendered = string.Join("|", result.Values.Values);

        Assert.DoesNotContain("Rutherford", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("MRN-99887766", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("123-45-6789", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("asha.verma@example.com", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("98765", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void SafeHarbor_PostCode_KeepsAtMostThreeDigits()
    {
        // §164.514(b)(2)(i)(B): geography finer than the first three ZIP
        // digits re-identifies.
        DeidentificationResult result = Deidentifier.ApplySafeHarbor(ClinicalRecord(), Policy());

        Assert.Equal("560", result.Values["PostCode"]);
    }

    [Fact]
    public void SafeHarbor_NonNumericPostCode_IsRemovedRatherThanGuessedAt()
    {
        var record = new Dictionary<string, object?>(StringComparer.Ordinal) { ["PostCode"] = "SW1A 1AA" };
        var policy = new SafeHarborPolicy(new Dictionary<string, SafeHarborIdentifier>(StringComparer.Ordinal)
        {
            ["PostCode"] = SafeHarborIdentifier.GeographicSubdivision,
        });

        DeidentificationResult result = Deidentifier.ApplySafeHarbor(record, policy);

        Assert.Equal(SafeHarborDeidentifier.RemovedMarker, result.Values["PostCode"]);
    }

    [Fact]
    public void SafeHarbor_ExplicitlyNonIdentifyingFields_SurviveIntact()
    {
        // De-identified data must remain useful, or nobody will use it and
        // they will keep querying the identified store instead.
        DeidentificationResult result = Deidentifier.ApplySafeHarbor(ClinicalRecord(), Policy());

        Assert.Equal("Type 2 diabetes mellitus", result.Values["Diagnosis"]);
        Assert.Equal(138, result.Values["SystolicBp"]);
    }

    [Fact]
    public void SafeHarbor_UnmappedField_IsRemovedByDefault()
    {
        // Fail closed. A field nobody classified is a field nobody checked,
        // and Safe Harbor requires the absence of all eighteen categories.
        Dictionary<string, object?> record = ClinicalRecord();
        record["UnclassifiedNote"] = "patient is the CEO of Acme Corp";

        DeidentificationResult result = Deidentifier.ApplySafeHarbor(record, Policy());

        Assert.Equal(SafeHarborDeidentifier.RemovedMarker, result.Values["UnclassifiedNote"]);
        Assert.Contains("UnclassifiedNote", result.UnmappedFields);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void SafeHarbor_UnmappedField_ReportedAsIncompletePolicy()
    {
        Dictionary<string, object?> record = ClinicalRecord();
        record["Extra"] = "x";

        DeidentificationResult complete = Deidentifier.ApplySafeHarbor(ClinicalRecord(), Policy());
        DeidentificationResult incomplete = Deidentifier.ApplySafeHarbor(record, Policy());

        Assert.True(complete.IsComplete);
        Assert.False(incomplete.IsComplete);
    }

    [Fact]
    public void SafeHarbor_RemovedIdentifiers_AreReportedAsAuditEvidence()
    {
        DeidentificationResult result = Deidentifier.ApplySafeHarbor(ClinicalRecord(), Policy());

        Assert.Contains(SafeHarborIdentifier.Name, result.RemovedIdentifiers);
        Assert.Contains(SafeHarborIdentifier.MedicalRecordNumber, result.RemovedIdentifiers);
        Assert.Contains(SafeHarborIdentifier.SocialSecurityNumber, result.RemovedIdentifiers);
    }

    [Fact]
    public void SafeHarbor_SubjectToken_IsRetainedSoTheRecordStaysLinkableToItself()
    {
        DeidentificationResult result = Deidentifier.ApplySafeHarbor(ClinicalRecord(), Policy());

        Assert.Equal("tok-abc123", result.Values["SubjectToken"]);
    }

    // ── date shifting ──────────────────────────────────────────────────────

    [Fact]
    public void ShiftDate_SameSubject_ShiftsEveryDateByTheSameOffset()
    {
        // The clinically important property: intervals survive. "The fever
        // started three days before admission" must still be true.
        SafeHarborDeidentifier deidentifier = Deidentifier;
        var birth = new DateTime(1984, 3, 14);
        var admission = new DateTime(2026, 2, 1);

        DateTime shiftedBirth = deidentifier.ShiftDate(birth, "tok-abc123");
        DateTime shiftedAdmission = deidentifier.ShiftDate(admission, "tok-abc123");

        Assert.Equal(admission - birth, shiftedAdmission - shiftedBirth);
    }

    [Fact]
    public void ShiftDate_DifferentSubjects_ShiftByDifferentOffsets()
    {
        // A shared offset would let one known subject's real dates unlock
        // everyone else's.
        SafeHarborDeidentifier deidentifier = Deidentifier;
        var date = new DateTime(2026, 2, 1);

        DateTime first = deidentifier.ShiftDate(date, "tok-aaa");
        DateTime second = deidentifier.ShiftDate(date, "tok-bbb");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ShiftDate_Always_IsDeterministicForTheSameSubject()
    {
        SafeHarborDeidentifier deidentifier = Deidentifier;
        var date = new DateTime(2026, 2, 1);

        Assert.Equal(
            deidentifier.ShiftDate(date, "tok-abc123"),
            deidentifier.ShiftDate(date, "tok-abc123"));
    }

    [Fact]
    public void ShiftDate_DifferentSalts_ProduceDifferentOffsets()
    {
        // The salt is what makes the shift irreversible without separate
        // control of the key.
        var a = new SafeHarborDeidentifier(Encoding.UTF8.GetBytes("salt-one"));
        var b = new SafeHarborDeidentifier(Encoding.UTF8.GetBytes("salt-two"));
        var date = new DateTime(2026, 2, 1);

        Assert.NotEqual(a.ShiftDate(date, "tok"), b.ShiftDate(date, "tok"));
    }

    [Fact]
    public void ShiftDate_Always_StaysWithinTheDeclaredBound()
    {
        SafeHarborDeidentifier deidentifier = Deidentifier;
        var date = new DateTime(2026, 2, 1);

        for (int i = 0; i < 500; i++)
        {
            DateTime shifted = deidentifier.ShiftDate(
                date, "tok-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));

            Assert.True(Math.Abs((shifted - date).TotalDays) <= SafeHarborDeidentifier.MaxDateShiftDays);
        }
    }

    [Fact]
    public void SafeHarbor_DateWithoutASubjectToken_ReducesToYearOnly()
    {
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["AdmissionDate"] = new DateTime(2026, 2, 1),
        };
        var policy = new SafeHarborPolicy(new Dictionary<string, SafeHarborIdentifier>(StringComparer.Ordinal)
        {
            ["AdmissionDate"] = SafeHarborIdentifier.DateElement,
        });

        DeidentificationResult result = Deidentifier.ApplySafeHarbor(record, policy);

        Assert.Equal("2026", result.Values["AdmissionDate"]);
    }

    [Theory]
    [InlineData(45, "45")]
    [InlineData(89, "89")]
    [InlineData(90, SafeHarborDeidentifier.AggregatedAge)]
    [InlineData(103, SafeHarborDeidentifier.AggregatedAge)]
    public void SafeHarbor_AgeOver89_IsAggregated(int age, string expected)
    {
        // §164.514(b)(2)(i)(C): ages above 89 identify, because the
        // population is small enough.
        var record = new Dictionary<string, object?>(StringComparer.Ordinal) { ["Age"] = age };
        var policy = new SafeHarborPolicy(new Dictionary<string, SafeHarborIdentifier>(StringComparer.Ordinal)
        {
            ["Age"] = SafeHarborIdentifier.DateElement,
        });

        DeidentificationResult result = Deidentifier.ApplySafeHarbor(record, policy);

        Assert.Equal(expected, result.Values["Age"]);
    }

    [Fact]
    public void Constructor_WithoutSalt_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new SafeHarborDeidentifier([]));
        Assert.Throws<ArgumentException>(() => new SafeHarborDeidentifier(null!));
    }

    [Fact]
    public void SafeHarborIdentifiers_CoverAllEighteenCategories()
    {
        // Enumerable, so "we handle all eighteen" is checkable rather than
        // claimed. Eighteen categories plus the explicit None.
        Assert.Equal(19, Enum.GetValues<SafeHarborIdentifier>().Length);
        Assert.Equal(18, (int)SafeHarborIdentifier.OtherUniqueIdentifier);
    }
}
