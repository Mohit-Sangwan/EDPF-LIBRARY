using Edpf.Abstractions.Primitives;

namespace Edpf.UnitTests.TestDoubles;

/// <summary>
/// Deterministic clock (Z.7: no wall-clock reads in unit tests). Time only
/// moves when a test moves it, so a rotation-overlap expiry can be tested
/// without sleeping.
/// </summary>
public sealed class FakeClock : IClock
{
    /// <summary>The current instant. Settable; tests advance it explicitly.</summary>
    public DateTimeOffset UtcNow { get; set; } = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Moves the clock forward.</summary>
    /// <param name="delta">How far to advance.</param>
    public void Advance(TimeSpan delta) => UtcNow = UtcNow.Add(delta);
}
