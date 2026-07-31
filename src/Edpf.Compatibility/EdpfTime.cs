using System;

namespace Edpf.Compatibility;

/// <summary>
/// The single sanctioned system-time source. Tier 1/2 targets read through
/// <c>TimeProvider.System</c>; Tier 3 (.NET Framework) falls back to
/// the BCL clock. Everything above this boundary consumes
/// <c>Edpf.Abstractions.Primitives.IClock</c> — direct system-time reads
/// anywhere else are a build failure (rule EDPF0003).
/// </summary>
public static class EdpfTime
{
    /// <summary>The current instant in UTC from the platform time source.</summary>
    public static DateTimeOffset UtcNow
#if NET8_0_OR_GREATER
        => TimeProvider.System.GetUtcNow();
#else
        => DateTimeOffset.UtcNow; // Sanctioned: the one raw read, behind the polyfill boundary.
#endif
}
