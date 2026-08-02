using System;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Connectors;

/// <summary>
/// How far an incremental sync has progressed (Phase 26f).
/// </summary>
/// <remarks>
/// <para>
/// **A timestamp alone is not a cursor, and this is the defect nearly every
/// bespoke integration ships.**
/// </para>
/// <para>
/// Given a watermark of <c>12:00:00</c> and three records all stamped
/// <c>12:00:00</c>, a query using <c>modified &gt; watermark</c> loses all
/// three, permanently and silently. Switching to <c>&gt;=</c> fixes the loss
/// and re-reads every record at the boundary on every run, forever — which is
/// survivable only if the whole pipeline downstream is idempotent, and it
/// usually is not.
/// </para>
/// <para>
/// The fix is a composite cursor. The comparison becomes
/// <c>(ts &gt; last.Ts) OR (ts = last.Ts AND id &gt; last.Id)</c>, which is
/// both gap-free and duplicate-free even when a thousand records share a
/// timestamp. It costs one extra column in the cursor and an index that leads
/// with the same two fields.
/// </para>
/// </remarks>
public sealed class SyncCursor : IEquatable<SyncCursor>
{
    /// <summary>Initializes a cursor.</summary>
    /// <param name="timestamp">The last record's modification time.</param>
    /// <param name="lastId">
    /// The last record's identifier, breaking ties within
    /// <paramref name="timestamp"/>. Compared ordinally.
    /// </param>
    public SyncCursor(DateTimeOffset timestamp, string lastId)
    {
        Timestamp = timestamp;
        LastId = Guard.NotNull(lastId, nameof(lastId));
    }

    /// <summary>A cursor positioned before any record.</summary>
    /// <remarks>
    /// <see cref="DateTimeOffset.MinValue"/> with an empty id, so a first run
    /// reads everything. Explicit rather than a null cursor, because a null
    /// that means "start from the beginning" is one dereference away from
    /// meaning "start from now" and losing the entire back catalogue.
    /// </remarks>
    public static SyncCursor Beginning { get; } = new(DateTimeOffset.MinValue, string.Empty);

    /// <summary>The last record's modification time.</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>The last record's identifier, breaking ties within the timestamp.</summary>
    public string LastId { get; }

    /// <summary>
    /// Whether a record falls after this cursor and should be read.
    /// </summary>
    /// <param name="timestamp">The record's modification time.</param>
    /// <param name="id">The record's identifier.</param>
    /// <returns>Whether the record is new to this sync.</returns>
    /// <remarks>
    /// The composite comparison in one place, so no connector author has to
    /// reconstruct it — and so there is exactly one implementation to get
    /// right.
    /// </remarks>
    public bool IsAfter(DateTimeOffset timestamp, string id)
    {
        Guard.NotNull(id, nameof(id));

        if (timestamp > Timestamp)
        {
            return true;
        }

        if (timestamp < Timestamp)
        {
            return false;
        }

        // Ordinal, deliberately: a culture-aware comparison would order ids
        // differently on a server in another region, and the same record would
        // be read twice in one deployment and skipped in another (Phase 27).
        return string.CompareOrdinal(id, LastId) > 0;
    }

    /// <summary>
    /// Advances to a record, refusing to move backwards.
    /// </summary>
    /// <param name="timestamp">The record's modification time.</param>
    /// <param name="id">The record's identifier.</param>
    /// <returns>The advanced cursor, or a failure if the record precedes it.</returns>
    /// <remarks>
    /// A cursor that can move backwards will, the first time a source returns
    /// an unsorted page — and every record between the two positions is then
    /// read again. Refusing here turns an ordering bug in a connector into a
    /// visible failure rather than a duplicate storm.
    /// </remarks>
    public Result<SyncCursor> Advance(DateTimeOffset timestamp, string id)
    {
        Guard.NotNull(id, nameof(id));

        if (!IsAfter(timestamp, id))
        {
            return Result.Failure<SyncCursor>(new Error(
                ErrorCodes.ValidationFailed,
                "A sync cursor cannot move backwards. The source returned a record at or before the "
                + "current position, which means the page was not ordered by (modified, id).",
                ErrorCategory.Validation));
        }

        return Result.Success(new SyncCursor(timestamp, id));
    }

    /// <inheritdoc />
    public bool Equals(SyncCursor? other)
        => other is not null
            && Timestamp == other.Timestamp
            && string.Equals(LastId, other.LastId, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as SyncCursor);

    /// <inheritdoc />
    public override int GetHashCode()
        => (Timestamp.GetHashCode() * 397) ^ LastId.GetHashCode(StringComparison.Ordinal);

    /// <inheritdoc />
    public override string ToString()
        => Timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "|" + LastId;
}
