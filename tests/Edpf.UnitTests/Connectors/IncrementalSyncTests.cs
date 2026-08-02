using Edpf.Abstractions.Primitives;
using Edpf.Connectors;

namespace Edpf.UnitTests.Connectors;

/// <summary>
/// Phase 26f — the three defects nearly every bespoke integration ships.
/// </summary>
public sealed class IncrementalSyncTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    // ── defect 1: the timestamp-only watermark ─────────────────────────────

    [Fact]
    public void RecordsSharingATimestamp_AreNeitherLostNorDuplicated()
    {
        // The defect: with a watermark of 12:00:00 and three records all
        // stamped 12:00:00, `modified > watermark` loses all three
        // permanently and silently; `>=` re-reads the boundary forever.
        var cursor = new SyncCursor(Noon, "record-b");

        // Same instant, earlier id — already processed.
        Assert.False(cursor.IsAfter(Noon, "record-a"));

        // Same instant, itself — not re-read.
        Assert.False(cursor.IsAfter(Noon, "record-b"));

        // Same instant, later id — still to come, and not lost.
        Assert.True(cursor.IsAfter(Noon, "record-c"));
    }

    [Fact]
    public void AThousandRecordsAtOneInstant_AreEachReadExactlyOnce()
    {
        // The realistic version: a bulk update stamps an entire batch with one
        // timestamp. Walking the cursor across them must visit each once.
        var ids = new List<string>();
        for (int i = 0; i < 1_000; i++)
        {
            ids.Add($"record-{i:D4}");
        }

        SyncCursor cursor = SyncCursor.Beginning;
        var visited = new List<string>();

        foreach (string id in ids)
        {
            if (cursor.IsAfter(Noon, id))
            {
                visited.Add(id);
                cursor = cursor.Advance(Noon, id).Value;
            }
        }

        Assert.Equal(1_000, visited.Count);
        Assert.Equal(ids, visited);

        // And a second pass from the final cursor reads nothing.
        foreach (string id in ids)
        {
            Assert.False(cursor.IsAfter(Noon, id));
        }
    }

    [Fact]
    public void CursorCannotMoveBackwards()
    {
        // A cursor that can move backwards will, the first time a source
        // returns an unsorted page — and every record between the two
        // positions is read again.
        var cursor = new SyncCursor(Noon, "record-b");

        Result<SyncCursor> back = cursor.Advance(Noon.AddMinutes(-5), "record-z");

        Assert.True(back.IsFailure);
        Assert.Contains("cannot move backwards", back.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BeginningCursor_ReadsEverything()
    {
        // Explicit rather than a null cursor: a null meaning "start from the
        // beginning" is one dereference away from meaning "start from now" and
        // losing the entire back catalogue.
        Assert.True(SyncCursor.Beginning.IsAfter(DateTimeOffset.MinValue.AddTicks(1), "a"));
        Assert.True(SyncCursor.Beginning.IsAfter(Noon, "any"));
    }

    [Fact]
    public void IdComparison_IsOrdinal_NotCultureSensitive()
    {
        // A culture-aware comparison would order ids differently on a server
        // in another region — the same record read twice in one deployment and
        // skipped in another (Phase 27).
        var cursor = new SyncCursor(Noon, "I");

        Assert.Equal(
            string.CompareOrdinal("ı", "I") > 0,
            cursor.IsAfter(Noon, "ı"));
    }

    // ── defect 2: reading up to "now" ──────────────────────────────────────

    [Fact]
    public void LateCommittingTransaction_IsNotLost()
    {
        // The scenario: a transaction starts at 11:59:58, stamps its row
        // 11:59:58, and commits at 12:00:03. A sync running at 12:00:00 that
        // read up to 12:00:00 would set its watermark past that row before the
        // row was visible — and never read it. Not late: never.
        var planner = new WatermarkPlanner(TimeSpan.FromSeconds(30));

        SyncWindow window = planner.PlanNext(SyncCursor.Beginning, Noon);

        DateTimeOffset lateRow = Noon.AddSeconds(-2);

        // The window stops well before the row's timestamp, so this pass does
        // not advance past it...
        Assert.True(window.UpperBoundExclusive < lateRow);

        // ...and a later pass, once the lag has elapsed, picks it up.
        SyncWindow next = planner.PlanNext(SyncCursor.Beginning, Noon.AddMinutes(1));
        Assert.True(next.UpperBoundExclusive > lateRow);
    }

    [Fact]
    public void ZeroSafetyLag_IsRefusedAtConstruction()
    {
        // A zero lag does not merely risk losing records — under any
        // concurrency it guarantees it, and the loss is silent.
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new WatermarkPlanner(TimeSpan.Zero));

        Assert.Contains("permanent and silent", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeSafetyLag_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WatermarkPlanner(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void RecordNewerThanTheWindow_IsRefused()
    {
        // Belt and braces over the source's own filtering. A source that
        // mis-applies the bounds would otherwise advance the cursor past
        // records this pass never read, and the gap would be invisible from
        // then on.
        var planner = new WatermarkPlanner(TimeSpan.FromSeconds(30));
        SyncWindow window = planner.PlanNext(SyncCursor.Beginning, Noon);

        Result accepted = WatermarkPlanner.Accepts(window, Noon, "too-new");

        Assert.True(accepted.IsFailure);
        Assert.Contains("never be read", accepted.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordBeforeTheResumeCursor_IsRefused()
    {
        var planner = new WatermarkPlanner(TimeSpan.FromSeconds(30));
        SyncWindow window = planner.PlanNext(new SyncCursor(Noon.AddHours(-1), "x"), Noon);

        Result accepted = WatermarkPlanner.Accepts(window, Noon.AddHours(-2), "old");

        Assert.True(accepted.IsFailure);
        Assert.Contains("duplicate work", accepted.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordInsideTheWindow_IsAccepted()
    {
        var planner = new WatermarkPlanner(TimeSpan.FromSeconds(30));
        SyncWindow window = planner.PlanNext(new SyncCursor(Noon.AddHours(-1), "x"), Noon);

        Assert.True(WatermarkPlanner.Accepts(window, Noon.AddMinutes(-5), "inside").IsSuccess);
    }

    [Fact]
    public void WindowIsEmpty_WhenNoTimeHasPassed()
    {
        // A sync running more often than its own lag has nothing new to do,
        // and must not treat that as an error.
        var planner = new WatermarkPlanner(TimeSpan.FromMinutes(5));

        SyncWindow window = planner.PlanNext(new SyncCursor(Noon, "x"), Noon.AddMinutes(1));

        Assert.True(window.IsEmpty);
    }

    // ── defect 3: offset pagination over a live set ────────────────────────

    [Fact]
    public void OffsetPaginationOverAChangingSet_IsRefused()
    {
        // Read rows 0-99 then 100-199. A delete before position 50 shifts
        // everything up one, so the row formerly at 100 is now at 99 — and it
        // is never read. The sync completes successfully having skipped it.
        Result<PaginationPlan> plan = PaginationPlan.Offset(100, sourceIsFrozen: false);

        Assert.True(plan.IsFailure);
        Assert.Contains("silently skips records", plan.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OffsetPaginationOverAFrozenSet_IsAllowed()
    {
        // A nightly extract against a snapshot is a real situation. It just
        // has to be said, because the silent default of "offset is fine" is
        // how the skip happens.
        Result<PaginationPlan> plan = PaginationPlan.Offset(100, sourceIsFrozen: true);

        Assert.True(plan.IsSuccess);
        Assert.True(plan.Value.SourceIsFrozen);
    }

    [Fact]
    public void KeysetPagination_NeedsNoSuchAssertion()
    {
        Result<PaginationPlan> plan = PaginationPlan.Keyset(100);

        Assert.True(plan.IsSuccess);
        Assert.False(plan.Value.SourceIsFrozen);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(PaginationPlan.MaximumPageSize + 1)]
    public void UnreasonablePageSize_IsRefusedAtConfigurationTime(int pageSize)
    {
        // Refused when the connector is configured rather than when it runs.
        Assert.True(PaginationPlan.Keyset(pageSize).IsFailure);
    }
}
