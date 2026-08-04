using System;
using System.Collections.Generic;
using Edpf.Abstractions.Primitives;

namespace Edpf.Documents;

/// <summary>What a block of a document is. A closed set, on purpose.</summary>
public enum DocumentBlockKind
{
    /// <summary>A document title. At most one, and it must be first.</summary>
    Title = 0,

    /// <summary>A section heading.</summary>
    Heading = 1,

    /// <summary>A paragraph of prose.</summary>
    Paragraph = 2,

    /// <summary>A label and its value, rendered as a pair.</summary>
    Field = 3,
}

/// <summary>One block of a document template.</summary>
public sealed class DocumentBlock
{
    /// <summary>
    /// Declares a block.
    /// </summary>
    /// <param name="kind">What the block is.</param>
    /// <param name="text">
    /// The block's text, which may contain <c>{{placeholder}}</c> markers. For
    /// a <see cref="DocumentBlockKind.Field"/> this is the label.
    /// </param>
    /// <param name="valueTemplate">
    /// The value text for a <see cref="DocumentBlockKind.Field"/>; null
    /// otherwise.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// A field has no value template, or a non-field has one.
    /// </exception>
    public DocumentBlock(DocumentBlockKind kind, string text, string? valueTemplate = null)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));

        if (kind == DocumentBlockKind.Field && valueTemplate is null)
        {
            throw new ArgumentException("A field block requires a value template.", nameof(valueTemplate));
        }

        if (kind != DocumentBlockKind.Field && valueTemplate is not null)
        {
            throw new ArgumentException(
                "Only a field block has a value template.", nameof(valueTemplate));
        }

        Kind = kind;
        ValueTemplate = valueTemplate;
    }

    /// <summary>What the block is.</summary>
    public DocumentBlockKind Kind { get; }

    /// <summary>The block's text, or a field's label.</summary>
    public string Text { get; }

    /// <summary>A field's value template; null for other kinds.</summary>
    public string? ValueTemplate { get; }
}

/// <summary>
/// A document template: ordered blocks with declared placeholders, and
/// nothing that can compute.
/// </summary>
/// <remarks>
/// <para>
/// The same closed-grammar decision as ADR-026 for formulas and
/// <c>MessageTemplate</c> for messages, applied a third time because the
/// pressure is identical: a discharge-summary template is content an
/// operations team edits, and a template language rich enough to be useful is
/// rich enough to reach the object graph it is handed.
/// </para>
/// <para>
/// So: no conditionals, no loops, no expressions, no member access, and no
/// includes. A template that needs a conditional needs two templates, and the
/// code that chooses between them is code, reviewed as code.
/// </para>
/// </remarks>
public sealed class DocumentTemplate
{
    private const string OpenMarker = "{{";
    private const string CloseMarker = "}}";

    /// <summary>
    /// Defines and validates a template.
    /// </summary>
    /// <param name="templateId">The template's stable id.</param>
    /// <param name="blocks">The blocks, in render order.</param>
    /// <param name="placeholders">
    /// Every placeholder used. Must match the blocks exactly, in both
    /// directions.
    /// </param>
    /// <exception cref="ArgumentNullException">A collection argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// The id is blank, a marker is unbalanced, a placeholder name is not
    /// alphanumeric, the declaration and the text disagree, or a title is
    /// present and is not the first block.
    /// </exception>
    public DocumentTemplate(
        string templateId,
        IReadOnlyList<DocumentBlock> blocks,
        IReadOnlyList<string> placeholders)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            throw new ArgumentException("A template requires an id.", nameof(templateId));
        }

        TemplateId = templateId;
        Blocks = blocks ?? throw new ArgumentNullException(nameof(blocks));
        Placeholders = placeholders ?? throw new ArgumentNullException(nameof(placeholders));

        var used = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].Kind == DocumentBlockKind.Title && i != 0)
            {
                throw new ArgumentException(
                    "A title must be the first block. A document with a title in the middle is two documents.",
                    nameof(blocks));
            }

            Collect(blocks[i].Text, used, nameof(blocks));
            if (blocks[i].ValueTemplate is not null)
            {
                Collect(blocks[i].ValueTemplate!, used, nameof(blocks));
            }
        }

        var declared = new HashSet<string>(placeholders, StringComparer.Ordinal);

        foreach (string name in used)
        {
            if (!declared.Contains(name))
            {
                throw new ArgumentException(
                    "The template uses a placeholder that was not declared.", nameof(placeholders));
            }
        }

        foreach (string name in declared)
        {
            if (!used.Contains(name))
            {
                throw new ArgumentException(
                    "A declared placeholder does not appear in the template.", nameof(placeholders));
            }
        }
    }

    /// <summary>The template's stable id.</summary>
    public string TemplateId { get; }

    /// <summary>The blocks, in render order.</summary>
    public IReadOnlyList<DocumentBlock> Blocks { get; }

    /// <summary>Every placeholder used.</summary>
    public IReadOnlyList<string> Placeholders { get; }

    /// <summary>
    /// Substitutes values and reports the document's effective classification.
    /// </summary>
    /// <param name="values">A value for every declared placeholder.</param>
    /// <returns>
    /// The composed document, or a failure when a value is missing or
    /// unexpected.
    /// </returns>
    /// <remarks>
    /// A missing value is a refusal, not an empty substitution. On a discharge
    /// summary an empty field is not a cosmetic defect — "Allergies:" followed
    /// by nothing reads as "none known".
    /// </remarks>
    public Result<ComposedDocument> Compose(IReadOnlyDictionary<string, DocumentValue> values)
    {
        if (values is null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        foreach (string name in Placeholders)
        {
            if (!values.ContainsKey(name))
            {
                return Result.Failure<ComposedDocument>(new Error(
                    ErrorCodes.ValidationFailed,
                    "A document placeholder has no value. On a clinical document an empty field is not a "
                    + "blank — it reads as a negative finding.",
                    ErrorCategory.Validation));
            }
        }

        foreach (KeyValuePair<string, DocumentValue> supplied in values)
        {
            if (!Contains(Placeholders, supplied.Key))
            {
                return Result.Failure<ComposedDocument>(new Error(
                    ErrorCodes.ValidationFailed,
                    "A value was supplied for a placeholder this template does not have.",
                    ErrorCategory.Validation));
            }
        }

        DataClassificationLevel effective = DataClassificationLevel.Public;
        foreach (KeyValuePair<string, DocumentValue> supplied in values)
        {
            if (supplied.Value.Classification > effective)
            {
                effective = supplied.Value.Classification;
            }
        }

        var composed = new List<ComposedBlock>(Blocks.Count);
        foreach (DocumentBlock block in Blocks)
        {
            composed.Add(new ComposedBlock(
                block.Kind,
                Substitute(block.Text, values),
                block.ValueTemplate is null ? null : Substitute(block.ValueTemplate, values)));
        }

        return new ComposedDocument(TemplateId, composed, effective);
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

    private static string Substitute(string text, IReadOnlyDictionary<string, DocumentValue> values)
    {
        var builder = new System.Text.StringBuilder(text.Length);
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

            // Substituted text is never rescanned, so a value containing a
            // marker is literal output rather than a second substitution pass.
            builder.Append(values[name].Text);
            cursor = close + CloseMarker.Length;
        }

        return builder.ToString();
    }

    private static void Collect(string text, HashSet<string> into, string parameterName)
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

/// <summary>A value substituted into a document, carrying its classification.</summary>
public sealed class DocumentValue
{
    /// <summary>
    /// Declares a value.
    /// </summary>
    /// <param name="text">The text to substitute.</param>
    /// <param name="classification">What the text is.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public DocumentValue(string text, DataClassificationLevel classification)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Classification = classification;
    }

    /// <summary>The text to substitute.</summary>
    public string Text { get; }

    /// <summary>What the text is.</summary>
    public DataClassificationLevel Classification { get; }
}

/// <summary>A block after substitution.</summary>
public sealed class ComposedBlock
{
    /// <summary>Records a composed block.</summary>
    /// <param name="kind">What the block is.</param>
    /// <param name="text">The substituted text, or a field's label.</param>
    /// <param name="value">A field's substituted value; null otherwise.</param>
    public ComposedBlock(DocumentBlockKind kind, string text, string? value)
    {
        Kind = kind;
        Text = text;
        Value = value;
    }

    /// <summary>What the block is.</summary>
    public DocumentBlockKind Kind { get; }

    /// <summary>The substituted text, or a field's label.</summary>
    public string Text { get; }

    /// <summary>A field's substituted value; null otherwise.</summary>
    public string? Value { get; }
}

/// <summary>A document after substitution, before rendering to bytes.</summary>
public sealed class ComposedDocument
{
    /// <summary>Records a composed document.</summary>
    /// <param name="templateId">Which template produced it.</param>
    /// <param name="blocks">The composed blocks.</param>
    /// <param name="classification">The highest classification substituted into it.</param>
    public ComposedDocument(
        string templateId, IReadOnlyList<ComposedBlock> blocks, DataClassificationLevel classification)
    {
        TemplateId = templateId;
        Blocks = blocks;
        Classification = classification;
    }

    /// <summary>Which template produced it.</summary>
    public string TemplateId { get; }

    /// <summary>The composed blocks.</summary>
    public IReadOnlyList<ComposedBlock> Blocks { get; }

    /// <summary>The highest classification substituted into it.</summary>
    public DataClassificationLevel Classification { get; }
}
