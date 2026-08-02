using System;
using System.Collections.Generic;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Migration;

/// <summary>
/// Where a migration has reached (Phase 35b — the strangler-fig stages).
/// </summary>
/// <remarks>
/// Ordered, and every stage before <see cref="LegacyRetired"/> is reversible.
/// The value of naming them is that "can we still go back?" has an answer
/// somebody can look up at three in the morning, rather than being
/// reconstructed from memory during an incident.
/// </remarks>
public enum CutoverStage
{
    /// <summary>Legacy serves everything. The new system holds no data.</summary>
    LegacyOnly = 0,

    /// <summary>
    /// Historic data has been copied. Legacy still serves every read and
    /// write.
    /// </summary>
    Backfilled = 1,

    /// <summary>
    /// Writes go to both systems; legacy remains the source of truth for
    /// reads. Divergence becomes measurable here, which is the point.
    /// </summary>
    DualWrite = 2,

    /// <summary>
    /// The new system serves reads; both are still written. The last stage
    /// from which returning is a configuration change.
    /// </summary>
    NewSystemReads = 3,

    /// <summary>
    /// Legacy is no longer written. **The point of no return** — from here,
    /// going back means restoring legacy from a backup and losing everything
    /// written since.
    /// </summary>
    LegacyRetired = 4,
}

/// <summary>
/// Governs advancing and reversing a migration (Phase 35b).
/// </summary>
/// <remarks>
/// <para>
/// The risk register names *"nobody migrates off legacy"* as a critical risk.
/// The reason is not usually technical difficulty — it is that nobody will
/// authorise a step they cannot undo, and most migration tooling is silent
/// about which steps those are.
/// </para>
/// <para>
/// So this type does two things: it refuses to skip stages, and it treats
/// retiring legacy as a **separate, explicitly acknowledged** act rather than
/// one more increment.
/// </para>
/// </remarks>
public sealed class CutoverPlan
{
    private readonly List<string> _log = [];

    /// <summary>Initializes a plan.</summary>
    /// <param name="name">What is being migrated.</param>
    /// <param name="clock">Time source (Z.3 rule 4).</param>
    public CutoverPlan(string name, IClock clock)
    {
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name));
        Clock = Guard.NotNull(clock, nameof(clock));
        Stage = CutoverStage.LegacyOnly;
    }

    /// <summary>What is being migrated.</summary>
    public string Name { get; }

    /// <summary>The time source.</summary>
    public IClock Clock { get; }

    /// <summary>Where the migration has reached.</summary>
    public CutoverStage Stage { get; private set; }

    /// <summary>Every stage change, in order.</summary>
    public IReadOnlyList<string> Log => _log;

    /// <summary>Whether the migration can still be reversed without data loss.</summary>
    public bool IsReversible => Stage < CutoverStage.LegacyRetired;

    /// <summary>
    /// Advances one stage.
    /// </summary>
    /// <param name="reconciliation">
    /// The reconciliation supporting the advance. Required from
    /// <see cref="CutoverStage.DualWrite"/> onward.
    /// </param>
    /// <param name="performedBy">Who is advancing it.</param>
    /// <returns>Success, or a failure explaining what is not satisfied.</returns>
    /// <remarks>
    /// Stages advance one at a time. Skipping from <see cref="CutoverStage.Backfilled"/>
    /// straight to serving reads would mean the new system has never been
    /// observed to stay in step under live write traffic — which is the only
    /// thing dual-write is for.
    /// </remarks>
    public Result Advance(ReconciliationReport? reconciliation, string performedBy)
    {
        Guard.NotNullOrWhiteSpace(performedBy, nameof(performedBy));

        if (Stage == CutoverStage.LegacyRetired)
        {
            return Result.Failure(new Error(
                ErrorCodes.ValidationFailed,
                "The migration is complete; there is no further stage.",
                ErrorCategory.Validation));
        }

        CutoverStage next = Stage + 1;

        // From dual-write onward the new system is about to take on a role it
        // cannot take on unverified. Before that, there is nothing yet to
        // reconcile.
        if (next >= CutoverStage.NewSystemReads)
        {
            if (reconciliation is null)
            {
                return Result.Failure(new Error(
                    ErrorCodes.ValidationFailed,
                    $"Advancing to {next} requires a reconciliation. Serving reads from a system nobody "
                    + "has shown to hold the same data is the failure this whole stage sequence exists "
                    + "to prevent.",
                    ErrorCategory.Validation));
            }

            if (!reconciliation.IsEquivalent)
            {
                return Result.Failure(new Error(
                    ErrorCodes.ValidationFailed,
                    $"The reconciliation found {reconciliation.Differences.Count} difference(s). Resolve "
                    + "them or record an accepted-variance decision before advancing.",
                    ErrorCategory.Validation));
            }
        }

        if (next == CutoverStage.LegacyRetired)
        {
            return Result.Failure(new Error(
                ErrorCodes.ValidationFailed,
                "Retiring legacy is the point of no return and cannot be reached by advancing. Call "
                + $"{nameof(RetireLegacy)} with an explicit acknowledgement.",
                ErrorCategory.Validation));
        }

        Stage = next;
        Record($"advanced to {Stage} by {performedBy}");
        return Result.Success();
    }

    /// <summary>
    /// Returns to the previous stage.
    /// </summary>
    /// <param name="reason">Why. Recorded, because a reversal is the most informative event in a migration.</param>
    /// <param name="performedBy">Who is reversing it.</param>
    /// <returns>Success, or a failure when the migration is past the point of no return.</returns>
    public Result Reverse(string reason, string performedBy)
    {
        Guard.NotNullOrWhiteSpace(performedBy, nameof(performedBy));

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(new Error(
                ErrorCodes.ValidationFailed,
                "A reversal requires a reason; it is the most informative event a migration produces.",
                ErrorCategory.Validation));
        }

        if (Stage == CutoverStage.LegacyOnly)
        {
            return Result.Failure(new Error(
                ErrorCodes.ValidationFailed,
                "The migration has not started.",
                ErrorCategory.Validation));
        }

        if (!IsReversible)
        {
            // Stated rather than attempted. A reversal that half-succeeds
            // after legacy has stopped being written leaves two partial
            // systems and no source of truth.
            return Result.Failure(new Error(
                ErrorCodes.ValidationFailed,
                "Legacy has been retired. Reversing now means restoring it from backup and losing "
                + "everything written since, which is a recovery operation rather than a rollback.",
                ErrorCategory.Validation));
        }

        Stage--;
        Record($"reversed to {Stage} by {performedBy}: {reason}");
        return Result.Success();
    }

    /// <summary>
    /// Retires legacy — the irreversible step.
    /// </summary>
    /// <param name="reconciliation">The final reconciliation.</param>
    /// <param name="acknowledgement">
    /// An explicit acknowledgement that this cannot be undone. Must match
    /// <see cref="RequiredAcknowledgement"/>.
    /// </param>
    /// <param name="performedBy">Who is retiring it.</param>
    /// <returns>Success, or a failure.</returns>
    /// <remarks>
    /// A separate method taking a typed phrase, rather than one more
    /// <see cref="Advance"/>. The ceremony is the point: an irreversible step
    /// that looks identical to a reversible one will eventually be taken by
    /// someone who thought it was reversible.
    /// </remarks>
    public Result RetireLegacy(
        ReconciliationReport reconciliation, string acknowledgement, string performedBy)
    {
        Guard.NotNull(reconciliation, nameof(reconciliation));
        Guard.NotNullOrWhiteSpace(performedBy, nameof(performedBy));

        if (Stage != CutoverStage.NewSystemReads)
        {
            return Result.Failure(new Error(
                ErrorCodes.ValidationFailed,
                $"Legacy can only be retired from {CutoverStage.NewSystemReads}; the migration is at "
                + $"{Stage}.",
                ErrorCategory.Validation));
        }

        if (!string.Equals(acknowledgement, RequiredAcknowledgement, StringComparison.Ordinal))
        {
            return Result.Failure(new Error(
                ErrorCodes.ValidationFailed,
                $"Retiring legacy requires the exact acknowledgement '{RequiredAcknowledgement}'.",
                ErrorCategory.Validation));
        }

        if (!reconciliation.IsEquivalent)
        {
            return Result.Failure(new Error(
                ErrorCodes.ValidationFailed,
                $"The final reconciliation found {reconciliation.Differences.Count} difference(s). After "
                + "this step the legacy copy stops being updated, so an unresolved difference becomes "
                + "permanent.",
                ErrorCategory.Validation));
        }

        Stage = CutoverStage.LegacyRetired;
        Record($"legacy retired by {performedBy}; reconciliation: {reconciliation}");
        return Result.Success();
    }

    /// <summary>The phrase that must be typed to retire legacy.</summary>
    public const string RequiredAcknowledgement = "I understand this cannot be undone";

    private void Record(string entry)
        => _log.Add($"{Clock.UtcNow:O} {Name}: {entry}");
}
