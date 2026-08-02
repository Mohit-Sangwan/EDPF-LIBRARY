using System;

namespace Edpf.Core.Time;

/// <summary>
/// Normalises an instant to a precision every supported store round-trips
/// without loss.
/// </summary>
/// <remarks>
/// <para>
/// **A hash over a timestamp is only stable if the store gives the timestamp
/// back unchanged**, and the Tier A providers do not agree on how much of one
/// they keep.
/// </para>
/// <list type="bullet">
/// <item>.NET <see cref="DateTimeOffset"/> — 100 ns ticks.</item>
/// <item>SQL Server <c>datetime2(7)</c> — 100 ns. Round-trips exactly.</item>
/// <item>PostgreSQL <c>timestamptz</c> — 1 µs, and it **rounds** rather than
/// truncating: <c>.1234567</c> is stored as <c>.123457</c>.</item>
/// </list>
/// <para>
/// So a value hashed before the write and recomputed after the read differs on
/// PostgreSQL and matches on SQL Server. A tamper-evident audit chain built
/// that way verifies on one Tier A provider and fails on the other — every
/// time, not intermittently — and the failure looks exactly like tampering.
/// </para>
/// <para>
/// The fix is to make the value storable *before* it is hashed, so what is
/// hashed and what is stored are the same number on every provider. That is
/// the same discipline ADR-032 applies to migration comparison: normalise
/// once, deliberately, at a boundary someone chose — rather than discovering
/// later that a store normalised it for you.
/// </para>
/// </remarks>
public static class StorableInstant
{
    /// <summary>
    /// Ticks per microsecond — the coarsest resolution among supported stores.
    /// </summary>
    public const long TicksPerMicrosecond = 10;

    /// <summary>
    /// Rounds an instant to microsecond precision.
    /// </summary>
    /// <param name="instant">The instant.</param>
    /// <returns>The instant, safe to hash and store.</returns>
    /// <remarks>
    /// **Rounds rather than truncates, to match PostgreSQL.** Truncating here
    /// would still disagree with a store that rounds: an instant ending
    /// <c>.1234567</c> truncates to <c>.123456</c> and PostgreSQL would store
    /// <c>.123457</c>, which is the same defect one digit further down.
    /// Matching the store's own rule is what makes the round-trip exact.
    /// </remarks>
    public static DateTimeOffset Normalize(DateTimeOffset instant)
    {
        long remainder = instant.UtcTicks % TicksPerMicrosecond;
        if (remainder == 0)
        {
            return instant;
        }

        long adjustment = remainder >= (TicksPerMicrosecond / 2)
            ? TicksPerMicrosecond - remainder
            : -remainder;

        return instant.AddTicks(adjustment);
    }

    /// <summary>
    /// Whether an instant survives a round-trip through every supported store.
    /// </summary>
    /// <param name="instant">The instant.</param>
    /// <returns>Whether it is already at a storable precision.</returns>
    /// <remarks>
    /// For assertions and diagnostics. A value that is not storable must never
    /// reach a hash input, and saying so at the point of construction is
    /// cheaper than diagnosing an audit chain that will not verify.
    /// </remarks>
    public static bool IsStorable(DateTimeOffset instant)
        => instant.UtcTicks % TicksPerMicrosecond == 0;
}
