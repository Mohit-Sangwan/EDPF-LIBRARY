using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Edpf.Abstractions.Validation;

/// <summary>How serious a validation finding is (Phase 17 §④).</summary>
public enum ValidationSeverity
{
    /// <summary>Informational; the operation proceeds.</summary>
    Info = 0,

    /// <summary>The operation proceeds, but something is questionable.</summary>
    Warning = 1,

    /// <summary>The operation is refused.</summary>
    Error = 2,
}

/// <summary>
/// One validation finding (Phase 17). The type's central property is that it
/// **cannot carry attacker-supplied content**: it holds a field name, a rule
/// name, and a bounded, sanitised message — never the submitted value.
/// </summary>
/// <remarks>
/// Validation is a security control, not merely a UX one. A message that
/// echoes raw input turns a validation endpoint into a reflected-XSS vector,
/// a log-injection vector, and an oracle. Constructing this type strips
/// control characters and bounds length, so a failure response is safe by
/// construction rather than by careful message authoring.
/// </remarks>
public sealed class ValidationFailure
{
    /// <summary>Maximum message length. Unbounded messages are a DoS and log-flood vector.</summary>
    public const int MaxMessageLength = 200;

    /// <summary>
    /// Initializes a finding.
    /// </summary>
    /// <param name="fieldName">The field that failed. A structural name, never a value.</param>
    /// <param name="ruleName">Which rule failed, e.g. <c>required</c>, <c>maxLength</c>.</param>
    /// <param name="message">
    /// A safe description. Sanitised and truncated on construction; callers
    /// still must not interpolate submitted values into it.
    /// </param>
    /// <param name="severity">How serious the finding is.</param>
    /// <exception cref="ArgumentException"><paramref name="fieldName"/> or <paramref name="ruleName"/> is blank.</exception>
    public ValidationFailure(
        string fieldName,
        string ruleName,
        string message,
        ValidationSeverity severity = ValidationSeverity.Error)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            throw new ArgumentException("Field name must not be blank.", nameof(fieldName));
        }

        if (string.IsNullOrWhiteSpace(ruleName))
        {
            throw new ArgumentException("Rule name must not be blank.", nameof(ruleName));
        }

        FieldName = Sanitize(fieldName);
        RuleName = Sanitize(ruleName);
        Message = Sanitize(message ?? string.Empty);
        Severity = severity;
    }

    /// <summary>The field that failed.</summary>
    public string FieldName { get; }

    /// <summary>Which rule failed.</summary>
    public string RuleName { get; }

    /// <summary>A safe, bounded description.</summary>
    public string Message { get; }

    /// <summary>How serious the finding is.</summary>
    public ValidationSeverity Severity { get; }

    /// <summary>
    /// Strips control characters and markup delimiters, and bounds length.
    /// </summary>
    /// <remarks>
    /// Applied unconditionally rather than only to suspicious input: a
    /// sanitiser that runs sometimes is a sanitiser that will be forgotten.
    /// </remarks>
    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, MaxMessageLength));

        foreach (char c in value)
        {
            if (builder.Length >= MaxMessageLength)
            {
                builder.Append('…');
                break;
            }

            if (char.IsControl(c))
            {
                continue;
            }

            builder.Append(c switch
            {
                '<' => "&lt;",
                '>' => "&gt;",
                '&' => "&amp;",
                '"' => "&quot;",
                '\'' => "&#39;",
                _ => c.ToString(CultureInfo.InvariantCulture),
            });
        }

        return builder.ToString();
    }

    /// <summary>Formats as <c>field: rule</c>. Safe to log.</summary>
    public override string ToString() => FieldName + ": " + RuleName;
}

/// <summary>The outcome of validating one object.</summary>
public sealed class ValidationOutcome
{
    /// <summary>
    /// Initializes an outcome.
    /// </summary>
    /// <param name="failures">Every finding, of any severity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="failures"/> is null.</exception>
    public ValidationOutcome(IReadOnlyList<ValidationFailure> failures)
        => Failures = failures ?? throw new ArgumentNullException(nameof(failures));

    /// <summary>An outcome with no findings.</summary>
    public static ValidationOutcome Valid { get; } = new([]);

    /// <summary>Every finding.</summary>
    public IReadOnlyList<ValidationFailure> Failures { get; }

    /// <summary>True when no finding is an error. Warnings and info do not block.</summary>
    public bool IsValid
    {
        get
        {
            foreach (ValidationFailure failure in Failures)
            {
                if (failure.Severity == ValidationSeverity.Error)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
