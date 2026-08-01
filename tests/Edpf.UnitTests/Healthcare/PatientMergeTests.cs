using Edpf.Abstractions.Primitives;
using Edpf.Healthcare.Domain;
using Edpf.UnitTests.TestDoubles;

namespace Edpf.UnitTests.Healthcare;

/// <summary>
/// Phase 24b §⑤: "Patient merge/unmerge is tested for full reversibility — an
/// irreversible incorrect merge is a clinical-safety incident."
/// </summary>
public sealed class PatientMergeTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private readonly FakeClock _clock = new();

    private PatientMergeService Service => new(_clock);

    private Patient NewPatient(string mrn, Guid? tenant = null)
        => new(Guid.NewGuid(), tenant ?? TenantA, mrn, _clock.UtcNow);

    [Fact]
    public void Merge_TwoDuplicates_MarksThemAgainstTheSurvivor()
    {
        Patient survivor = NewPatient("MRN-001");
        Patient duplicate = NewPatient("MRN-002");

        Result<PatientMergeRecord> result = Service.Merge(
            survivor, [duplicate], "same person, verified by DOB and address", "steward-17");

        Assert.True(result.IsSuccess);
        Assert.True(duplicate.IsMerged);
        Assert.Equal(survivor.Id, duplicate.MergedIntoPatientId);
        Assert.False(survivor.IsMerged);
    }

    [Fact]
    public void Merge_Always_RequiresAWrittenJustification()
    {
        // A clinical-safety operation must be reviewable afterwards.
        Result<PatientMergeRecord> result = Service.Merge(
            NewPatient("MRN-001"), [NewPatient("MRN-002")], "  ", "steward-17");

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Merge_AcrossTenants_IsRefusedAsNotFound()
    {
        // Joining two organisations' records is the most severe outcome
        // available here, and it is refused with 404 semantics — existence is
        // not disclosed across the boundary.
        Result<PatientMergeRecord> result = Service.Merge(
            NewPatient("MRN-001", TenantA),
            [NewPatient("MRN-002", TenantB)],
            "looks like the same person",
            "steward-17");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.TenantScopeViolation, result.Error!.Code);
    }

    [Fact]
    public void Merge_RecordIntoItself_IsRefused()
    {
        Patient patient = NewPatient("MRN-001");

        Result<PatientMergeRecord> result = Service.Merge(
            patient, [patient], "duplicate", "steward-17");

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Merge_AlreadyMergedRecord_IsRefused()
    {
        // A → B, then A → C. A is already merged.
        Patient a = NewPatient("MRN-001");
        Patient b = NewPatient("MRN-002");
        Patient c = NewPatient("MRN-003");

        Service.Merge(b, [a], "first merge", "steward-17");

        Result<PatientMergeRecord> second = Service.Merge(c, [a], "second merge", "steward-17");

        Assert.True(second.IsFailure);
    }

    [Fact]
    public void Merge_SurvivorOfAnEarlierMerge_IsRefused()
    {
        // A → B, then B → C. The direction that a guard on IsMerged alone does
        // not catch: at the second merge B is a survivor, not a merged record.
        // Reversing merge 1 afterwards would restore A against a survivor that
        // has itself been absorbed.
        Patient a = NewPatient("MRN-001");
        Patient b = NewPatient("MRN-002");
        Patient c = NewPatient("MRN-003");

        Service.Merge(b, [a], "first merge", "steward-17");

        Result<PatientMergeRecord> second = Service.Merge(c, [b], "second merge", "steward-17");

        Assert.True(second.IsFailure);
        Assert.Equal(ErrorCodes.Duplicate, second.Error!.Code);
        Assert.False(b.IsMerged);
    }

    [Fact]
    public void Merge_AfterAnEarlierMergeIsReversed_IsAllowed()
    {
        // The chain guard must block a chain, not permanently disqualify a
        // record from ever being merged again.
        Patient a = NewPatient("MRN-001");
        Patient b = NewPatient("MRN-002");
        Patient c = NewPatient("MRN-003");

        PatientMergeService service = Service;
        PatientMergeRecord first = service.Merge(b, [a], "first merge", "steward-17").Value;
        service.Unmerge(first, b, [a], "incorrect", "steward-04");

        Result<PatientMergeRecord> second = service.Merge(c, [b], "b and c are the same person", "steward-17");

        Assert.True(second.IsSuccess);
        Assert.True(b.IsMerged);
    }

    // ── reversibility ──────────────────────────────────────────────────────

    [Fact]
    public void Unmerge_AfterMerge_RestoresEveryRecordToIndependence()
    {
        // The property that makes an incorrect merge survivable.
        Patient survivor = NewPatient("MRN-001");
        Patient first = NewPatient("MRN-002");
        Patient second = NewPatient("MRN-003");

        PatientMergeRecord record = Service.Merge(
            survivor, [first, second], "verified duplicates", "steward-17").Value;

        _clock.Advance(TimeSpan.FromHours(2));
        Result reversed = Service.Unmerge(
            record, survivor, [first, second], "merge was incorrect; different people", "steward-04");

        Assert.True(reversed.IsSuccess);
        Assert.False(first.IsMerged);
        Assert.False(second.IsMerged);
        Assert.True(record.IsReversed);

        // Restored, not approximated: the survivor must stop claiming them too,
        // or a later merge would still see it as a survivor.
        Assert.False(survivor.IsMergeSurvivor);
        Assert.Equal(0, survivor.AbsorbedRecordCount);
    }

    [Fact]
    public void Unmerge_AgainstTheWrongSurvivor_IsRefused()
    {
        // Would restore the records while the real survivor still claims them.
        Patient survivor = NewPatient("MRN-001");
        Patient duplicate = NewPatient("MRN-002");
        Patient unrelated = NewPatient("MRN-003");
        PatientMergeRecord record = Service.Merge(
            survivor, [duplicate], "verified duplicate", "steward-17").Value;

        Result reversed = Service.Unmerge(record, unrelated, [duplicate], "incorrect", "steward-04");

        Assert.True(reversed.IsFailure);
        Assert.True(duplicate.IsMerged);
        Assert.False(record.IsReversed);
    }

    [Fact]
    public void Unmerge_Always_RecordsWhoAndWhy()
    {
        Patient survivor = NewPatient("MRN-001");
        Patient duplicate = NewPatient("MRN-002");
        PatientMergeRecord record = Service.Merge(
            survivor, [duplicate], "verified duplicate", "steward-17").Value;

        Service.Unmerge(
            record, survivor, [duplicate], "patient complained; records are different people", "steward-04");

        Assert.Equal("steward-04", record.ReversedBy);
        Assert.Equal("patient complained; records are different people", record.ReversalJustification);
        Assert.NotNull(record.ReversedUtc);
    }

    [Fact]
    public void Unmerge_PartialSetOfRecords_IsRefused()
    {
        // A partial unmerge leaves records pointing at a survivor that no
        // longer claims them — worse than the incorrect merge was.
        Patient survivor = NewPatient("MRN-001");
        Patient first = NewPatient("MRN-002");
        Patient second = NewPatient("MRN-003");

        PatientMergeRecord record = Service.Merge(
            survivor, [first, second], "verified duplicates", "steward-17").Value;

        Result reversed = Service.Unmerge(record, survivor, [first], "partial attempt", "steward-04");

        Assert.True(reversed.IsFailure);
        Assert.True(first.IsMerged);
        Assert.True(second.IsMerged);
    }

    [Fact]
    public void Unmerge_Twice_IsRefused()
    {
        Patient survivor = NewPatient("MRN-001");
        Patient duplicate = NewPatient("MRN-002");
        PatientMergeRecord record = Service.Merge(
            survivor, [duplicate], "verified duplicate", "steward-17").Value;

        PatientMergeService service = Service;
        service.Unmerge(record, survivor, [duplicate], "incorrect", "steward-04");
        Result second = service.Unmerge(record, survivor, [duplicate], "again", "steward-04");

        Assert.True(second.IsFailure);
    }

    [Fact]
    public void Unmerge_WithoutJustification_IsRefused()
    {
        Patient survivor = NewPatient("MRN-001");
        Patient duplicate = NewPatient("MRN-002");
        PatientMergeRecord record = Service.Merge(
            survivor, [duplicate], "verified duplicate", "steward-17").Value;

        Result reversed = Service.Unmerge(record, survivor, [duplicate], "  ", "steward-04");

        Assert.True(reversed.IsFailure);
        Assert.False(record.IsReversed);
        Assert.True(duplicate.IsMerged);
    }

    [Fact]
    public void MergeRecord_Always_RetainsWhatIsNeededToReverseIt()
    {
        // A pointer alone would not be enough: the unmerge needs to know
        // exactly which records participated.
        Patient survivor = NewPatient("MRN-001");
        Patient first = NewPatient("MRN-002");
        Patient second = NewPatient("MRN-003");

        PatientMergeRecord record = Service.Merge(
            survivor, [first, second], "verified duplicates", "steward-17").Value;

        Assert.Equal(survivor.Id, record.SurvivorId);
        Assert.Equal(2, record.MergedIds.Count);
        Assert.Contains(first.Id, record.MergedIds);
        Assert.Contains(second.Id, record.MergedIds);
        Assert.Equal("steward-17", record.PerformedBy);
    }

    [Fact]
    public void Patient_ClassifiedFields_AreTaggedSoTheCoreProtectsThemAutomatically()
    {
        // The layering claim, asserted: the vertical declares what its data
        // *is*, and inherits encryption, redaction, audit and export control
        // from the core. It implements none of them.
        var redactor = new Edpf.Diagnostics.Redaction.SensitiveDataRedactor();

        Assert.True(redactor.CarriesClassifiedData(typeof(Patient)));
    }
}
