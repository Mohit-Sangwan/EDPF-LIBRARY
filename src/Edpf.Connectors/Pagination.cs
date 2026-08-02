using System;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Connectors;

/// <summary>How a source pages its results (Phase 26f).</summary>
public enum PaginationStyle
{
    /// <summary>
    /// Each page resumes from the last record's key. Stable while the
    /// underlying set changes.
    /// </summary>
    Keyset = 0,

    /// <summary>
    /// Each page is requested by row offset. **Unstable while the underlying
    /// set changes** — see <see cref="PaginationPlan"/>.
    /// </summary>
    Offset = 1,

    /// <summary>The source issues an opaque continuation token.</summary>
    OpaqueToken = 2,
}

/// <summary>
/// How a connector will page through a source, and whether that is safe
/// (Phase 26f).
/// </summary>
/// <remarks>
/// <para>
/// **The third defect every bespoke integration ships: offset pagination over
/// a live set.**
/// </para>
/// <para>
/// Read rows 0-99, then 100-199. If a record is inserted before position 50
/// between the two requests, everything shifts down one — the record formerly
/// at 99 is now at 100 and gets read twice, and nothing is lost. If a record
/// is *deleted* before position 50, everything shifts up one, and the record
/// formerly at 100 is now at 99: **it is never read.** The sync completes
/// successfully having silently skipped a row.
/// </para>
/// <para>
/// Keyset pagination is immune: each page resumes from the last key seen, so
/// inserts and deletes elsewhere in the set cannot shift the boundary.
/// </para>
/// <para>
/// Offset is therefore permitted only when the caller states that the set is
/// frozen for the duration — a nightly extract against a snapshot, say. That
/// is a real situation, so it is allowed; it just has to be *said*, because a
/// silent default of "offset is fine" is how the skip happens.
/// </para>
/// </remarks>
public sealed class PaginationPlan
{
    private PaginationPlan(PaginationStyle style, int pageSize, bool sourceIsFrozen)
    {
        Style = style;
        PageSize = pageSize;
        SourceIsFrozen = sourceIsFrozen;
    }

    /// <summary>How the source pages.</summary>
    public PaginationStyle Style { get; }

    /// <summary>Records per page.</summary>
    public int PageSize { get; }

    /// <summary>Whether the caller has asserted the set cannot change mid-read.</summary>
    public bool SourceIsFrozen { get; }

    /// <summary>
    /// Plans keyset pagination, which is safe over a changing set.
    /// </summary>
    /// <param name="pageSize">Records per page.</param>
    /// <returns>The plan.</returns>
    public static Result<PaginationPlan> Keyset(int pageSize)
        => Validate(pageSize) ?? Result.Success(
            new PaginationPlan(PaginationStyle.Keyset, pageSize, sourceIsFrozen: false));

    /// <summary>
    /// Plans opaque-token pagination.
    /// </summary>
    /// <param name="pageSize">Records per page.</param>
    /// <returns>The plan.</returns>
    /// <remarks>
    /// Safety depends on the source's implementation, which cannot be
    /// inspected from here. Permitted because refusing it would exclude most
    /// commercial APIs, and recorded so an audit can see which connectors rest
    /// on a supplier's guarantee.
    /// </remarks>
    public static Result<PaginationPlan> OpaqueToken(int pageSize)
        => Validate(pageSize) ?? Result.Success(
            new PaginationPlan(PaginationStyle.OpaqueToken, pageSize, sourceIsFrozen: false));

    /// <summary>
    /// Plans offset pagination, which requires the set to be frozen.
    /// </summary>
    /// <param name="pageSize">Records per page.</param>
    /// <param name="sourceIsFrozen">
    /// The caller's assertion that the set cannot change for the duration of
    /// the read — a snapshot, an exported file, a table under an exclusive
    /// lock.
    /// </param>
    /// <returns>The plan, or a failure when the set is not frozen.</returns>
    public static Result<PaginationPlan> Offset(int pageSize, bool sourceIsFrozen)
    {
        Result<PaginationPlan>? invalid = Validate(pageSize);
        if (invalid is not null)
        {
            return invalid;
        }

        if (!sourceIsFrozen)
        {
            return Result.Failure<PaginationPlan>(new Error(
                ErrorCodes.ValidationFailed,
                "Offset pagination over a set that can change silently skips records: a delete before "
                + "the current offset shifts every later row up one, and the row that moves across the "
                + "page boundary is never read. Use keyset pagination, or state that the source is "
                + "frozen for the duration of the read.",
                ErrorCategory.Validation));
        }

        return Result.Success(new PaginationPlan(PaginationStyle.Offset, pageSize, sourceIsFrozen: true));
    }

    private static Result<PaginationPlan>? Validate(int pageSize)
    {
        if (pageSize <= 0)
        {
            return Result.Failure<PaginationPlan>(new Error(
                ErrorCodes.ValidationFailed, "A page size must be positive.", ErrorCategory.Validation));
        }

        if (pageSize > MaximumPageSize)
        {
            // A page the source will refuse, or one large enough to exhaust
            // memory on the reader. Either way the connector fails at run
            // time rather than at configuration time.
            return Result.Failure<PaginationPlan>(new Error(
                ErrorCodes.ValidationFailed,
                $"A page size above {MaximumPageSize} is refused by most sources and risks exhausting "
                + "the reader's memory.",
                ErrorCategory.Validation));
        }

        return null;
    }

    /// <summary>The largest page size permitted.</summary>
    public const int MaximumPageSize = 10_000;
}
