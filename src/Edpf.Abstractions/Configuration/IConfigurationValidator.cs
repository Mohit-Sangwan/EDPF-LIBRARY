using System.Collections.Generic;

namespace Edpf.Abstractions.Configuration;

/// <summary>
/// Validates a bound options object at startup (Phase 03 §④). The
/// application fails fast at boot on misconfiguration rather than at 3 a.m.
/// on the first use of a rarely-hit path.
/// </summary>
/// <typeparam name="TOptions">The options type being validated.</typeparam>
public interface IConfigurationValidator<in TOptions>
    where TOptions : class
{
    /// <summary>
    /// Validates <paramref name="options"/>.
    /// </summary>
    /// <param name="options">The bound options instance.</param>
    /// <returns>
    /// The failures found; empty means valid. Messages name the offending
    /// configuration key and the rule — **never the value**, which may be a
    /// secret (EDPF-CFG-8001).
    /// </returns>
    IReadOnlyList<string> Validate(TOptions options);
}
