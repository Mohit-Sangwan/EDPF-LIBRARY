using Edpf.Core.Time;

namespace Edpf.UnitTests.Time;

/// <summary>
/// The cross-provider timestamp defect found by running the Tier A parity
/// suite for the first time.
/// </summary>
/// <remarks>
/// A hash over a timestamp is only stable if the store returns the timestamp
/// unchanged. SQL Server keeps 100 ns ticks; PostgreSQL keeps microseconds and
/// rounds. So an audit chain hashing an un-normalised instant verifies on one
/// Tier A provider and fails on the other, every time.
/// </remarks>
public sealed class StorableInstantTests
{
    private static DateTimeOffset AtTicks(long fractionalTicks)
        => new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero).AddTicks(fractionalTicks);

    [Fact]
    public void SubMicrosecondInstant_IsNotStorable()
    {
        // The value that breaks the chain: 1,234,567 ticks is .1234567 s,
        // which PostgreSQL cannot represent.
        Assert.False(StorableInstant.IsStorable(AtTicks(1_234_567)));
    }

    [Fact]
    public void NormalizedInstant_IsStorable()
    {
        Assert.True(StorableInstant.IsStorable(StorableInstant.Normalize(AtTicks(1_234_567))));
    }

    [Fact]
    public void Normalize_RoundsToMatchPostgres_RatherThanTruncating()
    {
        // Empirically confirmed against postgres:16-alpine:
        //   '2026-08-02 12:00:00.1234567+00'::timestamptz  ->  .123457
        // Truncating to .123456 would still disagree with the store, which is
        // the same defect one digit further down.
        DateTimeOffset normalized = StorableInstant.Normalize(AtTicks(1_234_567));

        Assert.Equal(1_234_570, normalized.UtcTicks % TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void Normalize_RoundsDownBelowTheHalfway()
    {
        Assert.Equal(
            1_234_560,
            StorableInstant.Normalize(AtTicks(1_234_564)).UtcTicks % TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void Normalize_RoundsUpAtTheHalfway()
    {
        Assert.Equal(
            1_234_570,
            StorableInstant.Normalize(AtTicks(1_234_565)).UtcTicks % TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void AlreadyStorableInstant_IsUnchanged()
    {
        // Idempotent, so normalising twice cannot drift the value.
        DateTimeOffset exact = AtTicks(1_234_560);

        Assert.Equal(exact, StorableInstant.Normalize(exact));
        Assert.Equal(exact, StorableInstant.Normalize(StorableInstant.Normalize(exact)));
    }

    [Fact]
    public void Normalize_PreservesTheOffset()
    {
        // Rounding must not silently move the instant into another zone; the
        // hash covers UtcTicks, but a shifted offset would corrupt display and
        // retention arithmetic downstream.
        var withOffset = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(5.5)).AddTicks(7);

        Assert.Equal(TimeSpan.FromHours(5.5), StorableInstant.Normalize(withOffset).Offset);
    }

    [Fact]
    public void EverySubMicrosecondRemainder_NormalizesToAStorableValue()
    {
        for (long remainder = 0; remainder < StorableInstant.TicksPerMicrosecond; remainder++)
        {
            Assert.True(
                StorableInstant.IsStorable(StorableInstant.Normalize(AtTicks(1_234_560 + remainder))),
                $"remainder {remainder} did not normalize to a storable value.");
        }
    }
}
