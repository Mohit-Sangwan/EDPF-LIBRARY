using System;
using System.Collections.Generic;
using Edpf.Abstractions.Data;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Healthcare.Domain;

/// <summary>
/// A national identifier scheme, with the validation the classifier already
/// knows how to check (Phase 24b §④ <c>Edpf.Healthcare.Identity</c>).
/// </summary>
public enum NationalIdentifierScheme
{
    /// <summary>No national identifier recorded.</summary>
    None = 0,

    /// <summary>UK NHS number.</summary>
    NhsNumber = 1,

    /// <summary>India Aadhaar.</summary>
    Aadhaar = 2,

    /// <summary>India ABHA (Ayushman Bharat Health Account).</summary>
    Abha = 3,

    /// <summary>US Social Security number.</summary>
    SocialSecurityNumber = 4,
}

/// <summary>
/// A patient (Phase 24b §④). Every identifying field is classified, so the
/// core's encryption, redaction, audit and export controls apply
/// automatically — **the vertical author writes none of that.**
/// </summary>
/// <remarks>
/// This type is the demonstration that the layering works: it declares what
/// its data *is*, and inherits every protection from the core's declarative
/// machinery. If a vertical had to implement encryption itself, the core's
/// extension model would have failed.
/// </remarks>
public sealed class Patient : ITenantScopedEntity, IAuditableEntity, ISoftDeletable
{
    /// <summary>Initializes a patient.</summary>
    /// <param name="id">Patient identifier.</param>
    /// <param name="tenantId">The owning tenant.</param>
    /// <param name="medicalRecordNumber">The MRN.</param>
    /// <param name="createdUtc">When the record was created.</param>
    public Patient(Guid id, Guid tenantId, string medicalRecordNumber, DateTimeOffset createdUtc)
    {
        Id = Guard.NotDefault(id, nameof(id));
        TenantId = Guard.NotDefault(tenantId, nameof(tenantId));
        MedicalRecordNumber = Guard.NotNullOrWhiteSpace(medicalRecordNumber, nameof(medicalRecordNumber));
        CreatedUtc = createdUtc;
    }

    /// <summary>Patient identifier.</summary>
    public Guid Id { get; }

    /// <inheritdoc />
    public Guid TenantId { get; }

    /// <summary>The medical record number. PHI, so encrypted at rest by the core.</summary>
    [DataClassification(DataClassificationLevel.Phi)]
    public string MedicalRecordNumber { get; }

    /// <summary>Given name.</summary>
    [DataClassification(DataClassificationLevel.Pii)]
    public string? GivenName { get; set; }

    /// <summary>Family name.</summary>
    [DataClassification(DataClassificationLevel.Pii)]
    public string? FamilyName { get; set; }

    /// <summary>Date of birth. PHI: with a postcode it re-identifies most people.</summary>
    [DataClassification(DataClassificationLevel.Phi)]
    public DateTime? DateOfBirth { get; set; }

    /// <summary>Which national identifier scheme applies, if any.</summary>
    public NationalIdentifierScheme IdentifierScheme { get; set; }

    /// <summary>
    /// The national identifier. Mandatory encryption (Phase 24b §④) — the
    /// classification attribute is what makes that automatic.
    /// </summary>
    [DataClassification(DataClassificationLevel.Phi)]
    public string? NationalIdentifier { get; set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedUtc { get; }

    /// <inheritdoc />
    public DateTimeOffset? ModifiedUtc { get; set; }

    /// <inheritdoc />
    public bool IsDeleted { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset? DeletedUtc { get; private set; }

    /// <summary>
    /// The record this patient was merged into, if any. A merged record is
    /// retained rather than deleted, because the merge must be reversible.
    /// </summary>
    public Guid? MergedIntoPatientId { get; private set; }

    /// <summary>True when this record has been merged into another.</summary>
    public bool IsMerged => MergedIntoPatientId.HasValue;

    /// <summary>
    /// How many records have been merged into this one.
    /// </summary>
    /// <remarks>
    /// Tracked because merge direction alone is not enough to detect a chain.
    /// Merging A into B and then B into C never sets <see cref="IsMerged"/> on
    /// B at the moment of the first merge — B is the survivor — so a guard that
    /// only inspects the incoming records lets the chain form.
    /// </remarks>
    public int AbsorbedRecordCount { get; private set; }

    /// <summary>True when other records have been merged into this one.</summary>
    public bool IsMergeSurvivor => AbsorbedRecordCount > 0;

    /// <summary>Records that one more patient has been merged into this one.</summary>
    public void AbsorbRecord() => AbsorbedRecordCount++;

    /// <summary>
    /// Records that one merged patient has been released from this one.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No records are absorbed. Releasing more than were absorbed would let a
    /// survivor look independent while merged records still point at it.
    /// </exception>
    public void ReleaseRecord()
    {
        if (AbsorbedRecordCount == 0)
        {
            throw new InvalidOperationException(
                "No records are merged into this one; there is nothing to release.");
        }

        AbsorbedRecordCount--;
    }

    /// <summary>
    /// Marks this record as merged into <paramref name="survivorId"/>.
    /// </summary>
    /// <param name="survivorId">The surviving record.</param>
    /// <param name="mergedUtc">When the merge occurred.</param>
    /// <exception cref="InvalidOperationException">
    /// The record is already merged, or the survivor is itself. Merging a
    /// record twice, or into itself, produces a chain no unmerge can unwind.
    /// </exception>
    public void MarkMergedInto(Guid survivorId, DateTimeOffset mergedUtc)
    {
        if (IsMerged)
        {
            throw new InvalidOperationException(
                "This record is already merged. Chained merges cannot be reliably unwound, so a second merge "
                + "is refused rather than recorded.");
        }

        if (survivorId == Id)
        {
            throw new InvalidOperationException("A record cannot be merged into itself.");
        }

        MergedIntoPatientId = Guard.NotDefault(survivorId, nameof(survivorId));
        ModifiedUtc = mergedUtc;
    }

    /// <summary>
    /// Reverses a merge, restoring this record to independence.
    /// </summary>
    /// <param name="unmergedUtc">When the reversal occurred.</param>
    /// <exception cref="InvalidOperationException">The record is not merged.</exception>
    public void ReverseMerge(DateTimeOffset unmergedUtc)
    {
        if (!IsMerged)
        {
            throw new InvalidOperationException("This record is not merged; there is nothing to reverse.");
        }

        MergedIntoPatientId = null;
        ModifiedUtc = unmergedUtc;
    }

    /// <summary>Soft-deletes the record.</summary>
    /// <param name="deletedUtc">When the deletion occurred.</param>
    public void SoftDelete(DateTimeOffset deletedUtc)
    {
        IsDeleted = true;
        DeletedUtc = deletedUtc;
    }
}

/// <summary>
/// A record of one merge, kept so the operation is reversible
/// (Phase 24b §⑤).
/// </summary>
/// <remarks>
/// **Merging two patient records is a clinical-safety operation.** An
/// incorrect merge attaches one person's allergies, results and medications
/// to another; if it cannot be reversed, the harm is permanent. The full
/// pre-merge state is therefore retained, not just a pointer.
/// </remarks>
public sealed class PatientMergeRecord
{
    /// <summary>Initializes a merge record.</summary>
    /// <param name="mergeId">Identifies this merge, referenced by the audit trail.</param>
    /// <param name="tenantId">The owning tenant.</param>
    /// <param name="survivorId">The record that remains active.</param>
    /// <param name="mergedIds">The records merged into the survivor.</param>
    /// <param name="justification">Why the merge was performed. Mandatory and audited.</param>
    /// <param name="mergedUtc">When it occurred.</param>
    /// <param name="performedBy">Who performed it.</param>
    /// <exception cref="ArgumentException">A required value is missing, or no records were merged.</exception>
    public PatientMergeRecord(
        Guid mergeId,
        Guid tenantId,
        Guid survivorId,
        IReadOnlyList<Guid> mergedIds,
        string justification,
        DateTimeOffset mergedUtc,
        string performedBy)
    {
        MergeId = Guard.NotDefault(mergeId, nameof(mergeId));
        TenantId = Guard.NotDefault(tenantId, nameof(tenantId));
        SurvivorId = Guard.NotDefault(survivorId, nameof(survivorId));
        MergedIds = Guard.NotNull(mergedIds, nameof(mergedIds));

        if (mergedIds.Count == 0)
        {
            throw new ArgumentException("A merge must record at least one merged record.", nameof(mergedIds));
        }

        Justification = Guard.NotNullOrWhiteSpace(justification, nameof(justification));
        PerformedBy = Guard.NotNullOrWhiteSpace(performedBy, nameof(performedBy));
        MergedUtc = mergedUtc;
    }

    /// <summary>Identifies this merge.</summary>
    public Guid MergeId { get; }

    /// <summary>The owning tenant.</summary>
    public Guid TenantId { get; }

    /// <summary>The record that remains active.</summary>
    public Guid SurvivorId { get; }

    /// <summary>The records merged into the survivor.</summary>
    public IReadOnlyList<Guid> MergedIds { get; }

    /// <summary>Why the merge was performed.</summary>
    public string Justification { get; }

    /// <summary>Who performed it.</summary>
    public string PerformedBy { get; }

    /// <summary>When it occurred.</summary>
    public DateTimeOffset MergedUtc { get; }

    /// <summary>When it was reversed, if it was.</summary>
    public DateTimeOffset? ReversedUtc { get; private set; }

    /// <summary>Who reversed it.</summary>
    public string? ReversedBy { get; private set; }

    /// <summary>Why it was reversed.</summary>
    public string? ReversalJustification { get; private set; }

    /// <summary>True when this merge has been reversed.</summary>
    public bool IsReversed => ReversedUtc.HasValue;

    /// <summary>
    /// Records the reversal of this merge.
    /// </summary>
    /// <param name="reversedBy">Who reversed it.</param>
    /// <param name="justification">Why. Mandatory — an unmerge is as safety-critical as the merge.</param>
    /// <param name="reversedUtc">When.</param>
    /// <exception cref="InvalidOperationException">The merge is already reversed.</exception>
    public void RecordReversal(string reversedBy, string justification, DateTimeOffset reversedUtc)
    {
        if (IsReversed)
        {
            throw new InvalidOperationException("This merge has already been reversed.");
        }

        ReversedBy = Guard.NotNullOrWhiteSpace(reversedBy, nameof(reversedBy));
        ReversalJustification = Guard.NotNullOrWhiteSpace(justification, nameof(justification));
        ReversedUtc = reversedUtc;
    }
}
