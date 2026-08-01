using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Diagnostics;

/// <summary>
/// Removes classified data before it reaches a log sink (ADR-015).
/// **Redaction is opt-out, not opt-in** — a value is redacted unless it is
/// explicitly known to be safe. In a healthcare framework a log file is a
/// HIPAA-relevant artifact, so the default must fail closed.
/// </summary>
public interface ISensitiveDataRedactor
{
    /// <summary>
    /// Produces a log-safe representation of <paramref name="value"/>,
    /// walking nested objects, collections and dictionaries.
    /// </summary>
    /// <param name="value">The value about to be logged.</param>
    /// <returns>
    /// A representation with every classified member replaced by a redaction
    /// marker. Never returns the original instance when it carries classified
    /// data.
    /// </returns>
    object? Redact(object? value);

    /// <summary>
    /// Produces a log-safe string, additionally neutralising newline and
    /// control characters so a value cannot forge log entries (Phase 05 §⑥
    /// log-injection prevention).
    /// </summary>
    /// <param name="value">The text about to be logged.</param>
    /// <returns>Sanitised, redacted text.</returns>
    string RedactText(string? value);

    /// <summary>
    /// True when the type carries any member classified at or above
    /// <see cref="DataClassificationLevel.Confidential"/> — the check rule
    /// EDPF0005 performs at build time.
    /// </summary>
    /// <param name="type">The type to inspect.</param>
    bool CarriesClassifiedData(System.Type type);
}
