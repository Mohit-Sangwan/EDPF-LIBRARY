using System;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Connectors;

/// <summary>
/// The window one incremental sync pass will read (Phase 26f).
/// </summary>
public sealed class SyncWindow
{
    /// <summary>Initializes a window.</summary>
    /// <param name="resumeAfter">The cursor to read past.</param>
    /// <param name="upperBoundExclusive">The newest modification time this pass may read.</param>
    public SyncWindow(SyncCursor resumeAfter, DateTimeOffset upperBoundExclusive)
    {
        ResumeAfter = Guard.NotNull(resumeAfter, nameof(resumeAfter));
        UpperBoundExclusive = upperBoundExclusive;
    }

    /// <summary>The cursor to read past.</summary>
    public SyncCursor ResumeAfter { get; }

    /// <summary>
    /// The newest modification time this pass may read, exclusive.
    /// </summary>
    /// <remarks>
    /// Deliberately behind the source's current time. See
    /// <see cref="WatermarkPlanner"/> for why reading right up to "now" loses
    /// records permanently.
    /// </remarks>
    public DateTimeOffset UpperBoundExclusive { get; }

    /// <summary>Whether the window contains anything to read.</summary>
    public bool IsEmpty => UpperBoundExclusive <= ResumeAfter.Timestamp;
}

/// <summary>
/// Plans incremental sync windows that cannot silently lose records
/// (Phase 26f).
/// </summary>
/// <remarks>
/// <para>
/// **The second defect every bespoke integration ships: reading right up to
/// "now".**
/// </para>
/// <para>
/// Consider a source where a transaction starts at <c>11:59:58</c>, stamps its
/// row <c>modified = 11:59:58</c>, and commits at <c>12:00:03</c>. A sync
/// running at <c>12:00:00</c> reads everything up to <c>12:00:00</c> and sets
/// its watermark there. The row commits three seconds later, becomes visible,
/// and carries a timestamp *before* the watermark — so it is never read.
/// Not late: never. And nothing reports an error, because from the sync's
/// point of view everything went fine.
/// </para>
/// <para>
/// The same shape appears with clock skew between the source and the reader,
/// and with replica lag on a read replica.
/// </para>
/// <para>
/// The fix is to hold the upper bound back by a lag at least as long as the
/// source's longest plausible transaction, so a row cannot become visible with
/// a timestamp already behind the watermark. That costs latency — the sync is
/// always <c>lag</c> behind reality — and it is the only thing that makes the
/// sync complete.
/// </para>
/// </remarks>
public sealed class WatermarkPlanner
{
    /// <summary>Initializes a planner.</summary>
    /// <param name="safetyLag">
    /// How far behind the source's clock the upper bound is held. Must exceed
    /// the source's longest transaction plus any clock skew.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">The lag is zero or negative.</exception>
    public WatermarkPlanner(TimeSpan safetyLag)
    {
        if (safetyLag <= TimeSpan.Zero)
        {
            // Refused rather than defaulted. A zero lag does not merely risk
            // losing records — under any concurrency at all it guarantees it,
            // and the loss is silent. Making the caller state a number forces
            // them to think about their source's longest transaction.
            throw new ArgumentOutOfRangeException(
                nameof(safetyLag),
                safetyLag,
                "The safety lag must be positive. Reading up to the source's current time loses any row "
                + "whose transaction commits after the sync but carries an earlier timestamp, and the "
                + "loss is permanent and silent.");
        }

        SafetyLag = safetyLag;
    }

    /// <summary>
    /// A lag suitable for a source with ordinary short transactions.
    /// </summary>
    /// <remarks>
    /// Thirty seconds covers a typical OLTP transaction plus modest clock
    /// skew. It is a starting point, not a guarantee: a source that runs
    /// five-minute batch updates needs a lag longer than five minutes, and
    /// nothing here can discover that on the caller's behalf.
    /// </remarks>
    public static TimeSpan ConservativeDefault => TimeSpan.FromSeconds(30);

    /// <summary>How far behind the source's clock the upper bound is held.</summary>
    public TimeSpan SafetyLag { get; }

    /// <summary>
    /// Plans the next window.
    /// </summary>
    /// <param name="resumeAfter">Where the last pass finished.</param>
    /// <param name="sourceTimeNow">The source's current time, not the reader's.</param>
    /// <returns>The window to read.</returns>
    /// <remarks>
    /// Takes the **source's** clock. Using the reader's would reintroduce
    /// skew as the very error the lag exists to absorb, and a connector
    /// against a source in another region can differ by seconds.
    /// </remarks>
    public SyncWindow PlanNext(SyncCursor resumeAfter, DateTimeOffset sourceTimeNow)
    {
        Guard.NotNull(resumeAfter, nameof(resumeAfter));

        return new SyncWindow(resumeAfter, sourceTimeNow - SafetyLag);
    }

    /// <summary>
    /// Checks that a record belongs in the window before it is processed.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="timestamp">The record's modification time.</param>
    /// <param name="id">The record's identifier.</param>
    /// <returns>Success when the record belongs, or a failure explaining why not.</returns>
    /// <remarks>
    /// Belt and braces over the source's own filtering. A source that ignores
    /// or mis-applies the bounds would otherwise advance the cursor past
    /// records this pass never actually read — and the gap would be invisible
    /// from that point on.
    /// </remarks>
    public static Result Accepts(SyncWindow window, DateTimeOffset timestamp, string id)
    {
        Guard.NotNull(window, nameof(window));

        if (!window.ResumeAfter.IsAfter(timestamp, id))
        {
            return Result.Failure(new Error(
                ErrorCodes.ValidationFailed,
                "The source returned a record at or before the resume cursor. Processing it would "
                + "duplicate work already done.",
                ErrorCategory.Validation));
        }

        if (timestamp >= window.UpperBoundExclusive)
        {
            return Result.Failure(new Error(
                ErrorCodes.ValidationFailed,
                "The source returned a record newer than the window's upper bound. Accepting it would "
                + "advance the cursor past records whose transactions have not yet committed, and those "
                + "records would never be read.",
                ErrorCategory.Validation));
        }

        return Result.Success();
    }
}
