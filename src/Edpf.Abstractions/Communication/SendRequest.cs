using System;
using System.Collections.Generic;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Communication;

/// <summary>
/// A value substituted into a template, carrying its own classification.
/// </summary>
/// <remarks>
/// <para>
/// The classification travels with the value rather than being a property of
/// the template, and that is the whole mechanism. A reminder template is
/// harmless; the same template with a clinic name substituted into it discloses
/// a diagnosis. Only the caller knows which value they are passing.
/// </para>
/// <para>
/// This mirrors the formula engine, where classification propagates through
/// every operator rather than being asserted once about the expression.
/// </para>
/// </remarks>
public sealed class TemplateValue
{
    /// <summary>
    /// Declares a substitution value.
    /// </summary>
    /// <param name="value">The text to substitute.</param>
    /// <param name="classification">What that text is.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public TemplateValue(string value, DataClassificationLevel classification)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        Classification = classification;
    }

    /// <summary>The text to substitute.</summary>
    public string Value { get; }

    /// <summary>The classification of that text.</summary>
    public DataClassificationLevel Classification { get; }

    /// <summary>A value that discloses nothing on its own.</summary>
    /// <param name="value">The text.</param>
    public static TemplateValue Public(string value) => new(value, DataClassificationLevel.Public);
}

/// <summary>What to send, to whom, and on what basis.</summary>
public sealed class SendRequest
{
    /// <summary>
    /// Declares a send.
    /// </summary>
    /// <param name="templateId">The template to render.</param>
    /// <param name="recipient">The validated destination.</param>
    /// <param name="values">Values for the template's declared placeholders.</param>
    /// <param name="purpose">
    /// The declared processing purpose — <c>appointment-reminder</c>,
    /// <c>billing</c>. Checked against the subject's lawful basis; there is no
    /// default, because "we sent it for reasons" is not a purpose.
    /// </param>
    /// <param name="subjectToken">
    /// The pseudonymous data subject. Never a raw identifier, and never the
    /// recipient address — the consent record is keyed by subject, and the
    /// address is what consent is *about*.
    /// </param>
    /// <exception cref="ArgumentNullException">Any reference argument is null.</exception>
    /// <exception cref="ArgumentException">The template id, purpose or subject token is blank.</exception>
    public SendRequest(
        string templateId,
        MessageAddress recipient,
        IReadOnlyDictionary<string, TemplateValue> values,
        string purpose,
        string subjectToken)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            throw new ArgumentException("A send requires a template id.", nameof(templateId));
        }

        if (string.IsNullOrWhiteSpace(purpose))
        {
            throw new ArgumentException(
                "A send requires a declared purpose; consent is granted per purpose, not in general.",
                nameof(purpose));
        }

        if (string.IsNullOrWhiteSpace(subjectToken))
        {
            throw new ArgumentException("A send requires a subject token.", nameof(subjectToken));
        }

        TemplateId = templateId;
        Recipient = recipient ?? throw new ArgumentNullException(nameof(recipient));
        Values = values ?? throw new ArgumentNullException(nameof(values));
        Purpose = purpose;
        SubjectToken = subjectToken;
    }

    /// <summary>The template to render.</summary>
    public string TemplateId { get; }

    /// <summary>The validated destination.</summary>
    public MessageAddress Recipient { get; }

    /// <summary>Values for the template's declared placeholders.</summary>
    public IReadOnlyDictionary<string, TemplateValue> Values { get; }

    /// <summary>The declared processing purpose.</summary>
    public string Purpose { get; }

    /// <summary>The pseudonymous data subject.</summary>
    public string SubjectToken { get; }
}
