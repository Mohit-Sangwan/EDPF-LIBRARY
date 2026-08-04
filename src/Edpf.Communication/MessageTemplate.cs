using System;
using System.Collections.Generic;
using System.Text;
using Edpf.Abstractions.Communication;
using Edpf.Abstractions.Primitives;

namespace Edpf.Communication;

/// <summary>
/// A message template: fixed text plus declared placeholders, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// **This is a substitution grammar, not a template engine.** There are no
/// conditionals, no loops, no expressions and no member access — the same
/// closed-grammar decision ADR-026 made for formulas, for the same reason.
/// A template is content an operations team edits, and a template language
/// rich enough to be useful is rich enough to reach the object graph it is
/// handed.
/// </para>
/// <para>
/// Placeholders are declared at construction and checked against the text
/// immediately, so a typo in <c>{{patientName}}</c> fails when the template is
/// loaded rather than appearing verbatim in somebody's inbox.
/// </para>
/// </remarks>
public sealed class MessageTemplate
{
    private const string OpenMarker = "{{";
    private const string CloseMarker = "}}";

    /// <summary>
    /// Defines a template.
    /// </summary>
    /// <param name="templateId">The template's stable id.</param>
    /// <param name="channelName">The channel this template is written for.</param>
    /// <param name="subject">The subject line, which may contain placeholders.</param>
    /// <param name="body">The body, which may contain placeholders.</param>
    /// <param name="placeholders">
    /// Every placeholder the template uses. Must match the text exactly — an
    /// undeclared placeholder in the text and a declared one absent from it are
    /// both errors.
    /// </param>
    /// <exception cref="ArgumentNullException">Any reference argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// An id is blank, a marker is unbalanced, or the declared placeholders and
    /// the text disagree.
    /// </exception>
    public MessageTemplate(
        string templateId,
        string channelName,
        string subject,
        string body,
        IReadOnlyList<string> placeholders)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            throw new ArgumentException("A template requires an id.", nameof(templateId));
        }

        if (string.IsNullOrWhiteSpace(channelName))
        {
            throw new ArgumentException("A template requires a channel.", nameof(channelName));
        }

        TemplateId = templateId;
        ChannelName = channelName;
        Subject = subject ?? throw new ArgumentNullException(nameof(subject));
        Body = body ?? throw new ArgumentNullException(nameof(body));
        Placeholders = placeholders ?? throw new ArgumentNullException(nameof(placeholders));

        var used = new HashSet<string>(StringComparer.Ordinal);
        CollectPlaceholders(Subject, used, nameof(subject));
        CollectPlaceholders(Body, used, nameof(body));

        var declared = new HashSet<string>(placeholders, StringComparer.Ordinal);

        foreach (string name in used)
        {
            if (!declared.Contains(name))
            {
                throw new ArgumentException(
                    "The template text uses a placeholder that was not declared.", nameof(placeholders));
            }
        }

        foreach (string name in declared)
        {
            if (!used.Contains(name))
            {
                // A declared placeholder the text never uses means the two drifted
                // apart. Which of them is stale is not knowable here, and guessing
                // produces either a missing value or an unexplained one.
                throw new ArgumentException(
                    "A declared placeholder does not appear in the template text.", nameof(placeholders));
            }
        }
    }

    /// <summary>The template's stable id.</summary>
    public string TemplateId { get; }

    /// <summary>The channel this template is written for.</summary>
    public string ChannelName { get; }

    /// <summary>The subject line.</summary>
    public string Subject { get; }

    /// <summary>The body.</summary>
    public string Body { get; }

    /// <summary>Every placeholder the template uses.</summary>
    public IReadOnlyList<string> Placeholders { get; }

    /// <summary>
    /// Substitutes values and reports the classification of the result.
    /// </summary>
    /// <param name="values">A value for every declared placeholder.</param>
    /// <returns>
    /// The rendered subject, body and effective classification, or a failure
    /// when a value is missing or unexpected.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A missing value is a refusal, not an empty substitution. "Dear ," is a
    /// visible incident, and "your appointment is at " is a support call at
    /// best.
    /// </para>
    /// <para>
    /// An unexpected value is also a refusal. A caller supplying
    /// <c>diagnosis</c> to a template that has no such placeholder has almost
    /// certainly selected the wrong template, and silently discarding the value
    /// hides that until the day the templates are edited.
    /// </para>
    /// </remarks>
    public Result<RenderedMessage> Render(IReadOnlyDictionary<string, TemplateValue> values)
    {
        if (values is null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        foreach (string name in Placeholders)
        {
            if (!values.ContainsKey(name))
            {
                return Result.Failure<RenderedMessage>(new Error(
                    ErrorCodes.ValidationFailed,
                    "A template placeholder has no value. Rendering it empty is not a fallback.",
                    ErrorCategory.Validation));
            }
        }

        foreach (KeyValuePair<string, TemplateValue> supplied in values)
        {
            if (!Contains(Placeholders, supplied.Key))
            {
                return Result.Failure<RenderedMessage>(new Error(
                    ErrorCodes.ValidationFailed,
                    "A value was supplied for a placeholder this template does not have.",
                    ErrorCategory.Validation));
            }
        }

        // The effective classification is the highest of everything that went
        // in. A Public template carrying one PHI value is a PHI message; there
        // is no arithmetic under which it is not.
        DataClassificationLevel effective = DataClassificationLevel.Public;
        foreach (KeyValuePair<string, TemplateValue> supplied in values)
        {
            if (supplied.Value.Classification > effective)
            {
                effective = supplied.Value.Classification;
            }
        }

        return new RenderedMessage(
            Substitute(Subject, values),
            Substitute(Body, values),
            effective);
    }

    private static bool Contains(IReadOnlyList<string> names, string candidate)
    {
        for (int i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Substitute(string text, IReadOnlyDictionary<string, TemplateValue> values)
    {
        var builder = new StringBuilder(text.Length);
        int cursor = 0;

        while (cursor < text.Length)
        {
            int open = text.IndexOf(OpenMarker, cursor, StringComparison.Ordinal);
            if (open < 0)
            {
                builder.Append(text, cursor, text.Length - cursor);
                break;
            }

            builder.Append(text, cursor, open - cursor);

            int close = text.IndexOf(CloseMarker, open, StringComparison.Ordinal);
            string name = text.Substring(open + OpenMarker.Length, close - open - OpenMarker.Length);

            // Substituted text is never re-scanned. A value containing
            // "{{amount}}" is that literal text in the output, not a second
            // round of substitution — otherwise a caller-supplied value could
            // reach a placeholder the caller was not given.
            builder.Append(values[name].Value);
            cursor = close + CloseMarker.Length;
        }

        return builder.ToString();
    }

    private static void CollectPlaceholders(string text, HashSet<string> into, string parameterName)
    {
        int cursor = 0;

        while (cursor < text.Length)
        {
            int open = text.IndexOf(OpenMarker, cursor, StringComparison.Ordinal);
            if (open < 0)
            {
                return;
            }

            int close = text.IndexOf(CloseMarker, open, StringComparison.Ordinal);
            if (close < 0)
            {
                throw new ArgumentException("The template has an unclosed placeholder marker.", parameterName);
            }

            string name = text.Substring(open + OpenMarker.Length, close - open - OpenMarker.Length);
            if (name.Length == 0)
            {
                throw new ArgumentException("The template has an empty placeholder.", parameterName);
            }

            foreach (char c in name)
            {
                bool legal = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
                if (!legal)
                {
                    throw new ArgumentException(
                        "Placeholder names are alphanumeric. Anything richer is an expression, and an "
                        + "expression language in a template is a scripting host.",
                        parameterName);
                }
            }

            into.Add(name);
            cursor = close + CloseMarker.Length;
        }
    }
}

/// <summary>The result of rendering a template.</summary>
public sealed class RenderedMessage
{
    /// <summary>
    /// Records a rendering.
    /// </summary>
    /// <param name="subject">The rendered subject.</param>
    /// <param name="body">The rendered body.</param>
    /// <param name="classification">The highest classification that went into it.</param>
    public RenderedMessage(string subject, string body, DataClassificationLevel classification)
    {
        Subject = subject;
        Body = body;
        Classification = classification;
    }

    /// <summary>The rendered subject.</summary>
    public string Subject { get; }

    /// <summary>The rendered body.</summary>
    public string Body { get; }

    /// <summary>The effective classification of the rendered content.</summary>
    public DataClassificationLevel Classification { get; }
}
