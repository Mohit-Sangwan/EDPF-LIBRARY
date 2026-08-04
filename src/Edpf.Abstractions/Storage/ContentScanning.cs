using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Storage;

/// <summary>What a scanner concluded about a payload.</summary>
public enum ScanVerdict
{
    /// <summary>The scanner examined the content and found nothing.</summary>
    Clean = 0,

    /// <summary>The scanner identified malicious content.</summary>
    Infected = 1,

    /// <summary>
    /// The scanner could not reach a conclusion — it timed out, the archive was
    /// encrypted, the file was too large, or the engine errored.
    /// </summary>
    /// <remarks>
    /// **This is not a soft <see cref="Clean"/>.** Treating "I could not tell"
    /// as "it is fine" is how a password-protected archive walks past a
    /// scanner, and it is the single most common way this control is defeated
    /// in practice.
    /// </remarks>
    Indeterminate = 2,
}

/// <summary>What is known about a stored blob's scan state.</summary>
public enum ScanState
{
    /// <summary>No scanner was configured when this blob was written.</summary>
    NotScanned = 0,

    /// <summary>A scanner examined it and found nothing.</summary>
    Clean = 1,
}

/// <summary>
/// A malware scanner. ClamAV, Defender, a cloud scanning API, or a test double.
/// </summary>
/// <remarks>
/// The framework ships no engine. Scanning is a signature-database business
/// with an update cadence measured in hours, and a framework that bundled one
/// would ship it stale (ADR-001).
/// </remarks>
public interface IContentScanner
{
    /// <summary>A stable name, recorded against the blob for audit.</summary>
    string ScannerName { get; }

    /// <summary>
    /// Examines content.
    /// </summary>
    /// <param name="content">The bytes to scan — plaintext, before encryption.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>
    /// The verdict, or a failure. A failure is treated exactly as
    /// <see cref="ScanVerdict.Indeterminate"/>.
    /// </returns>
    Task<Result<ScanVerdict>> ScanAsync(byte[] content, CancellationToken cancellationToken);
}

/// <summary>
/// Extracts searchable text from a stored artefact — OCR, a PDF text layer, a
/// document parser.
/// </summary>
/// <remarks>
/// <para>
/// **Extracted text inherits the blob's classification, without exception.**
/// The text of a scanned discharge summary is the discharge summary. This is
/// the seam where an OCR pipeline typically leaks: text goes to a search index
/// that was never told what it was handling.
/// </para>
/// </remarks>
public interface IContentExtractor
{
    /// <summary>A stable name for the audit trail.</summary>
    string ExtractorName { get; }

    /// <summary>Media types this extractor can read.</summary>
    IReadOnlyList<string> SupportedContentTypes { get; }

    /// <summary>
    /// Extracts text.
    /// </summary>
    /// <param name="content">The plaintext bytes.</param>
    /// <param name="contentType">The declared media type.</param>
    /// <param name="cancellationToken">Cancels the extraction.</param>
    /// <returns>
    /// The extracted text carrying the source's classification, or a failure.
    /// </returns>
    Task<Result<ExtractedContent>> ExtractAsync(
        byte[] content, string contentType, CancellationToken cancellationToken);
}

/// <summary>Text extracted from a blob, carrying the blob's classification.</summary>
public sealed class ExtractedContent
{
    /// <summary>
    /// Records extracted text.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="classification">
    /// The source blob's classification. There is no parameter to lower it,
    /// because extraction does not de-identify anything.
    /// </param>
    /// <param name="extractorName">Which extractor produced it.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public ExtractedContent(string text, DataClassificationLevel classification, string extractorName)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        ExtractorName = extractorName ?? throw new ArgumentNullException(nameof(extractorName));
        Classification = classification;
    }

    /// <summary>The extracted text.</summary>
    public string Text { get; }

    /// <summary>The source blob's classification, inherited.</summary>
    public DataClassificationLevel Classification { get; }

    /// <summary>Which extractor produced it.</summary>
    public string ExtractorName { get; }
}

/// <summary>
/// A resumable, chunked upload.
/// </summary>
/// <remarks>
/// Large clinical artefacts — a DICOM study, a scanned records bundle — do not
/// survive a single request over hospital Wi-Fi. The session exists so a
/// failure costs one chunk rather than the whole transfer.
/// </remarks>
public interface IBlobUploadSession : IDisposable
{
    /// <summary>The session's id, used to resume.</summary>
    string SessionId { get; }

    /// <summary>Bytes accepted so far.</summary>
    long BytesReceived { get; }

    /// <summary>
    /// Appends a chunk.
    /// </summary>
    /// <param name="chunk">The bytes.</param>
    /// <param name="cancellationToken">Cancels the append.</param>
    /// <returns>
    /// Success, or a failure when the declared maximum would be exceeded. The
    /// limit is enforced per chunk, so an over-long upload is refused as it
    /// arrives rather than after it has all been buffered.
    /// </returns>
    Task<Result> AppendAsync(byte[] chunk, CancellationToken cancellationToken);

    /// <summary>
    /// Finalises the upload, applying every write-time control at once.
    /// </summary>
    /// <param name="cancellationToken">Cancels completion.</param>
    /// <returns>The descriptor of what was stored, or a failure.</returns>
    /// <remarks>
    /// Scanning, compression and encryption all happen here rather than per
    /// chunk. A scanner given one chunk of a file cannot see a signature that
    /// straddles a boundary, which is a well-known evasion.
    /// </remarks>
    Task<Result<BlobDescriptor>> CompleteAsync(CancellationToken cancellationToken);

    /// <summary>Abandons the upload and discards what was received.</summary>
    void Abort();
}
