using System;
using Edpf.Abstractions.Primitives;
using Edpf.Compatibility;

namespace Edpf.Core.Time;

/// <summary>
/// The production <see cref="IClock"/>: reads the platform time source through
/// the <see cref="EdpfTime"/> polyfill boundary. Tests substitute a fake clock;
/// production composition roots register this singleton.
/// </summary>
public sealed class SystemClock : IClock
{
    private SystemClock()
    {
    }

    /// <summary>The process-wide instance. Stateless and thread-safe.</summary>
    public static SystemClock Instance { get; } = new();

    /// <inheritdoc />
    public DateTimeOffset UtcNow => EdpfTime.UtcNow;
}
