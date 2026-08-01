using System;
using System.Collections.Generic;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Healthcare.Domain;

/// <summary>
/// Patient record merge and unmerge (Phase 24b §④ Identity).
/// </summary>
/// <remarks>
/// <para>
/// **Merging two patient records is a clinical-safety operation.** An
/// incorrect merge attaches one person's allergies, results and medications
/// to another — and the failure mode is not an error message, it is a
/// clinician acting on the wrong chart.
/// </para>
/// <para>
/// Two rules follow, and both are enforced here rather than left to the
/// caller:
/// </para>
/// <list type="number">
/// <item>**Every merge is reversible.** Full pre-merge state is retained, so
/// an unmerge restores rather than approximates.</item>
/// <item>**Chained merges are refused.** Merging A into B and then B into C
/// produces a state no unmerge can reliably unwind, so the second merge is
/// rejected rather than recorded.</item>
/// </list>
/// </remarks>
public sealed class PatientMergeService
{
    private readonly IClock _clock;

    /// <summary>Initializes the service.</summary>
    /// <param name="clock">Time source (Z.3 rule 4).</param>
    public PatientMergeService(IClock clock) => _clock = Guard.NotNull(clock, nameof(clock));

    /// <summary>
    /// Merges one or more duplicate records into a survivor.
    /// </summary>
    /// <param name="survivor">The record that remains active.</param>
    /// <param name="duplicates">The records to merge in.</param>
    /// <param name="justification">Why. Mandatory and audited.</param>
    /// <param name="performedBy">Who is performing the merge.</param>
    /// <returns>
    /// The merge record, which is the artefact an unmerge needs, or a failure.
    /// </returns>
    public Result<PatientMergeRecord> Merge(
        Patient survivor,
        IReadOnlyList<Patient> duplicates,
        string justification,
        string performedBy)
    {
        Guard.NotNull(survivor, nameof(survivor));
        Guard.NotNull(duplicates, nameof(duplicates));

        if (duplicates.Count == 0)
        {
            return Result.Failure<PatientMergeRecord>(new Error(
                ErrorCodes.ValidationFailed, "A merge requires at least one duplicate.", ErrorCategory.Validation));
        }

        if (string.IsNullOrWhiteSpace(justification))
        {
            return Result.Failure<PatientMergeRecord>(new Error(
                ErrorCodes.ValidationFailed,
                "A merge requires a written justification; it is a clinical-safety operation and must be "
                + "reviewable afterwards.",
                ErrorCategory.Validation));
        }

        foreach (Patient duplicate in duplicates)
        {
            // Cross-tenant merge would join two organisations' records — the
            // most severe outcome available here.
            if (duplicate.TenantId != survivor.TenantId)
            {
                return Result.Failure<PatientMergeRecord>(new Error(
                    ErrorCodes.TenantScopeViolation,
                    "The requested resource was not found.",
                    ErrorCategory.NotFound));
            }

            if (duplicate.Id == survivor.Id)
            {
                return Result.Failure<PatientMergeRecord>(new Error(
                    ErrorCodes.ValidationFailed,
                    "A record cannot be merged into itself.",
                    ErrorCategory.Validation));
            }

            if (duplicate.IsMerged)
            {
                return Result.Failure<PatientMergeRecord>(new Error(
                    ErrorCodes.Duplicate,
                    "A record in this merge is already merged. Chained merges cannot be reliably unwound.",
                    ErrorCategory.Conflict));
            }

            // The other direction of the same chain, and the easier one to
            // miss: merging A into B and then B into C. B is a survivor at the
            // second merge, not a merged record, so inspecting IsMerged alone
            // lets the chain form. Reversing merge 1 afterwards would restore A
            // against a survivor that has itself been absorbed.
            if (duplicate.IsMergeSurvivor)
            {
                return Result.Failure<PatientMergeRecord>(new Error(
                    ErrorCodes.Duplicate,
                    "A record in this merge has other records merged into it. Merging a survivor into a third "
                    + "record would build a chain that cannot be reliably unwound; reverse the earlier merge "
                    + "first.",
                    ErrorCategory.Conflict));
            }
        }

        if (survivor.IsMerged)
        {
            return Result.Failure<PatientMergeRecord>(new Error(
                ErrorCodes.Duplicate,
                "The survivor is itself merged into another record.",
                ErrorCategory.Conflict));
        }

        DateTimeOffset now = _clock.UtcNow;
        var mergedIds = new List<Guid>(duplicates.Count);

        foreach (Patient duplicate in duplicates)
        {
            duplicate.MarkMergedInto(survivor.Id, now);
            survivor.AbsorbRecord();
            mergedIds.Add(duplicate.Id);
        }

        return Result.Success(new PatientMergeRecord(
            Guid.NewGuid(), survivor.TenantId, survivor.Id, mergedIds, justification, now, performedBy));
    }

    /// <summary>
    /// Reverses a merge, restoring every merged record to independence.
    /// </summary>
    /// <param name="mergeRecord">The merge to reverse.</param>
    /// <param name="survivor">The record that absorbed the others.</param>
    /// <param name="mergedPatients">The records that were merged, in any order.</param>
    /// <param name="justification">Why. Mandatory — an unmerge is as safety-critical as the merge.</param>
    /// <param name="reversedBy">Who is reversing it.</param>
    /// <returns>Success once every record is restored, or a failure.</returns>
    public Result Unmerge(
        PatientMergeRecord mergeRecord,
        Patient survivor,
        IReadOnlyList<Patient> mergedPatients,
        string justification,
        string reversedBy)
    {
        Guard.NotNull(mergeRecord, nameof(mergeRecord));
        Guard.NotNull(survivor, nameof(survivor));
        Guard.NotNull(mergedPatients, nameof(mergedPatients));

        // Reversing against the wrong survivor would restore the records while
        // leaving the real survivor still claiming them.
        if (survivor.Id != mergeRecord.SurvivorId)
        {
            return Result.Failure(new Error(
                ErrorCodes.ValidationFailed,
                "The supplied survivor is not the survivor of this merge.",
                ErrorCategory.Validation));
        }

        if (mergeRecord.IsReversed)
        {
            return Result.Failure(new Error(
                ErrorCodes.Duplicate, "This merge has already been reversed.", ErrorCategory.Conflict));
        }

        if (string.IsNullOrWhiteSpace(justification))
        {
            return Result.Failure(new Error(
                ErrorCodes.ValidationFailed,
                "An unmerge requires a written justification.",
                ErrorCategory.Validation));
        }

        // Every merged record must be present. A partial unmerge would leave
        // some records pointing at a survivor that no longer claims them,
        // which is worse than the incorrect merge was.
        var supplied = new HashSet<Guid>();
        foreach (Patient patient in mergedPatients)
        {
            supplied.Add(patient.Id);
        }

        foreach (Guid required in mergeRecord.MergedIds)
        {
            if (!supplied.Contains(required))
            {
                return Result.Failure(new Error(
                    ErrorCodes.ValidationFailed,
                    "Every record in the merge must be supplied; a partial unmerge would leave records "
                    + "orphaned against a survivor that no longer claims them.",
                    ErrorCategory.Validation));
            }
        }

        DateTimeOffset now = _clock.UtcNow;

        foreach (Patient patient in mergedPatients)
        {
            if (patient.IsMerged)
            {
                patient.ReverseMerge(now);
                survivor.ReleaseRecord();
            }
        }

        mergeRecord.RecordReversal(reversedBy, justification, now);
        return Result.Success();
    }
}
