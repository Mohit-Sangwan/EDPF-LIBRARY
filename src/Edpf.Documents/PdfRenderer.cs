using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Documents;

/// <summary>Turns a composed document into bytes.</summary>
public interface IDocumentRenderer
{
    /// <summary>The media type this renderer produces.</summary>
    string ContentType { get; }

    /// <summary>A file extension, without the dot.</summary>
    string FileExtension { get; }

    /// <summary>
    /// Renders to bytes.
    /// </summary>
    /// <param name="document">The composed document.</param>
    /// <returns>The artefact bytes, or a failure.</returns>
    Result<byte[]> Render(ComposedDocument document);
}

/// <summary>
/// Writes a minimal, deterministic PDF 1.4 with **no active content of any
/// kind**.
/// </summary>
/// <remarks>
/// <para>
/// The storage layer's content-type allow-list excludes <c>application/pdf</c>
/// with the reasoning that a PDF is a program — it carries JavaScript, launch
/// actions, embedded files and external references. That judgement is about
/// PDFs arriving from outside.
/// </para>
/// <para>
/// **This renderer is the other side of it.** A document EDPF produces contains
/// no <c>/JavaScript</c>, no <c>/OpenAction</c>, no <c>/Launch</c>, no
/// <c>/EmbeddedFile</c>, no <c>/URI</c> and no <c>/AA</c>, and a test asserts
/// their absence over the emitted bytes. So the framework's own discharge
/// summaries are inert, and it is checkable rather than promised.
/// </para>
/// <para>
/// It is also **deterministic**: no creation date, no producer string, no
/// object-id randomness. The same document renders to the same bytes forever,
/// which is what lets a signature over those bytes mean something a year later
/// (see <see cref="DocumentSigningService"/>).
/// </para>
/// <para>
/// **Limitation, stated rather than hidden.** The single built-in font is
/// Helvetica with WinAnsi encoding, so text outside that repertoire is
/// <em>refused</em>, not transliterated. Silently turning a patient's name into
/// question marks on a legal document is worse than failing to produce it. Full
/// Unicode needs an embedded font subset, which is real work and is deferred.
/// </para>
/// </remarks>
public sealed class PdfRenderer : IDocumentRenderer
{
    private const int PageWidth = 595;   // A4 at 72 dpi
    private const int PageHeight = 842;
    private const int Margin = 56;
    private const int MaxLineChars = 88;

    /// <inheritdoc />
    public string ContentType => "application/pdf";

    /// <inheritdoc />
    public string FileExtension => "pdf";

    /// <inheritdoc />
    public Result<byte[]> Render(ComposedDocument document)
    {
        Guard.NotNull(document, nameof(document));

        var lines = new List<(string Text, int Size)>();

        foreach (ComposedBlock block in document.Blocks)
        {
            switch (block.Kind)
            {
                case DocumentBlockKind.Title:
                    AddWrapped(lines, block.Text, 18);
                    lines.Add((string.Empty, 11));
                    break;

                case DocumentBlockKind.Heading:
                    lines.Add((string.Empty, 11));
                    AddWrapped(lines, block.Text, 14);
                    break;

                case DocumentBlockKind.Field:
                    AddWrapped(lines, block.Text + ": " + block.Value, 11);
                    break;

                default:
                    AddWrapped(lines, block.Text, 11);
                    lines.Add((string.Empty, 11));
                    break;
            }
        }

        // The classification is stamped on the artefact itself. A printed
        // document leaves the system entirely, and the handling rule has to
        // travel on the paper because nothing else follows it there.
        lines.Add((string.Empty, 9));
        lines.Add(("Classification: " + document.Classification.ToString().ToUpperInvariant(), 9));

        var content = new StringBuilder();
        content.Append("BT\n");
        int y = PageHeight - Margin;

        foreach ((string text, int size) in lines)
        {
            if (y < Margin)
            {
                break;
            }

            if (text.Length > 0)
            {
                Result<string> escaped = EscapeText(text);
                if (escaped.IsFailure)
                {
                    return Result.Failure<byte[]>(escaped.Error!);
                }

                content.Append("/F1 ").Append(size.ToString(CultureInfo.InvariantCulture)).Append(" Tf\n");
                content.Append('1').Append(" 0 0 1 ")
                    .Append(Margin.ToString(CultureInfo.InvariantCulture)).Append(' ')
                    .Append(y.ToString(CultureInfo.InvariantCulture)).Append(" Tm\n");
                content.Append('(').Append(escaped.Value).Append(") Tj\n");
            }

            y -= size + 6;
        }

        content.Append("ET");

        return Assemble(content.ToString());
    }

    private static void AddWrapped(List<(string Text, int Size)> lines, string text, int size)
    {
        if (text.Length <= MaxLineChars)
        {
            lines.Add((text, size));
            return;
        }

        string remaining = text;
        while (remaining.Length > MaxLineChars)
        {
            int breakAt = remaining.LastIndexOf(' ', Math.Min(MaxLineChars, remaining.Length - 1));
            if (breakAt <= 0)
            {
                breakAt = MaxLineChars;
            }

            lines.Add((remaining.Substring(0, breakAt), size));
            remaining = remaining.Substring(breakAt).TrimStart();
        }

        if (remaining.Length > 0)
        {
            lines.Add((remaining, size));
        }
    }

    /// <summary>
    /// Escapes a string literal, refusing anything the built-in encoding cannot
    /// represent.
    /// </summary>
    /// <remarks>
    /// The escaping is what stops a value from closing the literal and being
    /// read as PDF operators — the same class of problem as SQL injection and
    /// CSV formula injection, in a format where the payload would be a page
    /// description rather than a query.
    /// </remarks>
    private static Result<string> EscapeText(string text)
    {
        var builder = new StringBuilder(text.Length + 8);

        foreach (char c in text)
        {
            if (c is '(' or ')' or '\\')
            {
                builder.Append('\\').Append(c);
                continue;
            }

            if (c is '\r' or '\n' or '\t')
            {
                builder.Append(' ');
                continue;
            }

            if (c < 32 || c > 255)
            {
                return Result.Failure<string>(new Error(
                    ErrorCodes.ValidationFailed,
                    "The document contains a character the built-in font cannot represent. It is refused "
                    + "rather than transliterated: a name silently rendered as question marks on a legal "
                    + "document is worse than a document that failed to render.",
                    ErrorCategory.Validation));
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static byte[] Assemble(string contentStream)
    {
        byte[] contentBytes = Encoding.ASCII.GetBytes(contentStream);

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + PageWidth.ToString(CultureInfo.InvariantCulture)
                + " " + PageHeight.ToString(CultureInfo.InvariantCulture)
                + "] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            "<< /Length " + contentBytes.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n"
                + contentStream + "\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };

        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");

        var offsets = new List<int>(objects.Count);
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n");
            pdf.Append(objects[i]).Append("\nendobj\n");
        }

        int xrefOffset = pdf.Length;
        pdf.Append("xref\n0 ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture)).Append('\n');
        pdf.Append("0000000000 65535 f \n");

        foreach (int offset in offsets)
        {
            pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        // No /Info dictionary. A creation date would make the bytes differ
        // between two renders of the same document, and a signature over
        // "the same document" would then not verify.
        pdf.Append("trailer\n<< /Size ")
            .Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture))
            .Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xrefOffset.ToString(CultureInfo.InvariantCulture))
            .Append("\n%%EOF");

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}

/// <summary>Renders to plain text. Diagnostics, previews and printers that take text.</summary>
public sealed class PlainTextRenderer : IDocumentRenderer
{
    /// <inheritdoc />
    public string ContentType => "text/plain";

    /// <inheritdoc />
    public string FileExtension => "txt";

    /// <inheritdoc />
    public Result<byte[]> Render(ComposedDocument document)
    {
        Guard.NotNull(document, nameof(document));

        var builder = new StringBuilder();

        foreach (ComposedBlock block in document.Blocks)
        {
            switch (block.Kind)
            {
                case DocumentBlockKind.Title:
                    builder.Append(block.Text).Append("\r\n");
                    builder.Append(new string('=', block.Text.Length)).Append("\r\n\r\n");
                    break;

                case DocumentBlockKind.Heading:
                    builder.Append("\r\n").Append(block.Text).Append("\r\n");
                    builder.Append(new string('-', block.Text.Length)).Append("\r\n");
                    break;

                case DocumentBlockKind.Field:
                    builder.Append(block.Text).Append(": ").Append(block.Value).Append("\r\n");
                    break;

                default:
                    builder.Append(block.Text).Append("\r\n\r\n");
                    break;
            }
        }

        builder.Append("\r\nClassification: ")
            .Append(document.Classification.ToString().ToUpperInvariant())
            .Append("\r\n");

        return Encoding.UTF8.GetBytes(builder.ToString());
    }
}
