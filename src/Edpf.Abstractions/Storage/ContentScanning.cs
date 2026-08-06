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

/// <summary>One key-value pair an extractor recognised on a form.</summary>
public sealed class ExtractedField
{
    /// <summary>
    /// Records a field.
    /// </summary>
    /// <param name="key">The label, as read.</param>
    /// <param name="value">The value, as read.</param>
    /// <param name="confidence">How sure the extractor is, from 0 to 1.</param>
    /// <exception cref="ArgumentNullException">The key or value is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="confidence"/> is outside 0 to 1.</exception>
    public ExtractedField(string key, string value, double confidence)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Value = value ?? throw new ArgumentNullException(nameof(value));

        if (confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(confidence), "Confidence is a probability from 0 to 1.");
        }

        Confidence = confidence;
    }

    /// <summary>The label, as read.</summary>
    public string Key { get; }

    /// <summary>The value, as read.</summary>
    public string Value { get; }

    /// <summary>
    /// How sure the extractor is, from 0 to 1.
    /// </summary>
    /// <remarks>
    /// Per field rather than per document, because that is where it matters: a
    /// discharge summary can be read at 0.98 overall while the one field
    /// carrying a medication dose was read at 0.41.
    /// </remarks>
    public double Confidence { get; }
}

/// <summary>A table an extractor recognised.</summary>
public sealed class ExtractedTable
{
    /// <summary>
    /// Records a table.
    /// </summary>
    /// <param name="rows">The cells, row by row.</param>
    /// <param name="confidence">How sure the extractor is, from 0 to 1.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="confidence"/> is outside 0 to 1.</exception>
    public ExtractedTable(IReadOnlyList<IReadOnlyList<string>> rows, double confidence)
    {
        Rows = rows ?? throw new ArgumentNullException(nameof(rows));

        if (confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(confidence), "Confidence is a probability from 0 to 1.");
        }

        Confidence = confidence;
    }

    /// <summary>The cells, row by row.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }

    /// <summary>How sure the extractor is, from 0 to 1.</summary>
    public double Confidence { get; }
}

/// <summary>
/// Everything an extractor read from a blob, carrying the blob's
/// classification.
/// </summary>
/// <remarks>
/// <para>
/// **Every part inherits the classification, not just the text.** A table of
/// laboratory values is as much PHI as the prose around it, and a key-value
/// pair reading "NHS number: …" more so. A result type that classified only
/// its text would leak through the structured half.
/// </para>
/// <para>
/// **Confidence is mandatory.** An OCR engine that cannot say how sure it is
/// has not given a usable answer for clinical use, and the alternative —
/// defaulting to 1.0 — asserts certainty on the engine's behalf.
/// </para>
/// </remarks>
public sealed class ExtractedContent
{
    /// <summary>
    /// Records an extraction.
    /// </summary>
    /// <param name="text">The full text.</param>
    /// <param name="classification">
    /// The source blob's classification. There is no parameter to lower it,
    /// because extraction does not de-identify anything.
    /// </param>
    /// <param name="extractorName">Which extractor produced it.</param>
    /// <param name="confidence">Overall confidence, from 0 to 1.</param>
    /// <param name="language">The detected language tag, or null when unknown.</param>
    /// <param name="fields">Recognised key-value pairs.</param>
    /// <param name="tables">Recognised tables.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="confidence"/> is outside 0 to 1.</exception>
    public ExtractedContent(
        string text,
        DataClassificationLevel classification,
        string extractorName,
        double confidence = 1.0,
        string? language = null,
        IReadOnlyList<ExtractedField>? fields = null,
        IReadOnlyList<ExtractedTable>? tables = null)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        ExtractorName = extractorName ?? throw new ArgumentNullException(nameof(extractorName));

        if (confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(confidence), "Confidence is a probability from 0 to 1.");
        }

        Classification = classification;
        Confidence = confidence;
        Language = language;
        Fields = fields ?? [];
        Tables = tables ?? [];
    }

    /// <summary>The full text.</summary>
    public string Text { get; }

    /// <summary>The source blob's classification, inherited.</summary>
    public DataClassificationLevel Classification { get; }

    /// <summary>Which extractor produced it.</summary>
    public string ExtractorName { get; }

    /// <summary>Overall confidence, from 0 to 1.</summary>
    public double Confidence { get; }

    /// <summary>
    /// The detected language tag, or null when the extractor could not say.
    /// </summary>
    /// <remarks>
    /// Null rather than a default of <c>en</c>. Assuming English is how a
    /// Bengali discharge note gets indexed with an English stemmer and then
    /// cannot be found by the people who need it.
    /// </remarks>
    public string? Language { get; }

    /// <summary>Recognised key-value pairs.</summary>
    public IReadOnlyList<ExtractedField> Fields { get; }

    /// <summary>Recognised tables.</summary>
    public IReadOnlyList<ExtractedTable> Tables { get; }

    /// <summary>
    /// True when any part of the extraction fell below the required confidence
    /// and a person must check it before it is relied on.
    /// </summary>
    /// <remarks>
    /// Set by the storage layer rather than by the extractor. The three
    /// dispositions match ADR-029's: a usable reading passes, a doubtful one is
    /// flagged for a human, and nothing is silently discarded. Dropping a
    /// low-confidence field would lose the fact that the document contained
    /// something there at all.
    /// </remarks>
    public bool RequiresHumanReview { get; private set; }

    /// <summary>Marks this extraction as needing human review.</summary>
    /// <remarks>Called by the storage layer after applying the confidence floor.</remarks>
    public void FlagForHumanReview() => RequiresHumanReview = true;
}

/// <summary>
/// A resumable, chunked upload.
/// </summary>
/// <remarks>
/// Large clinical artefacts — a DICOM study, a scanned records bundle — do not
/// survive a single request over hospital Wi-Fi. The session exists so a
/// failure costs one chunk rather than the whole transfer.
/// </remarks>
/// <summary>
/// A backend that can accept an upload in parts, without the caller holding
/// the whole payload.
/// </summary>
/// <remarks>
/// <para>
/// **Optional, and the fallback is honest about what it costs.** A backend
/// that does not implement this still supports chunked upload through
/// <see cref="IBlobUploadSession"/>, but the session then buffers — which for
/// a DICOM study defeats the purpose. Declaring the capability separately means
/// a deployment can find out which of its backends actually stream, rather
/// than discovering it from a memory graph.
/// </para>
/// <para>
/// The three methods map onto what the underlying services already do: S3
/// multipart upload, and SFTP writes at an explicit offset. Neither is
/// invented here.
/// </para>
/// </remarks>
public interface IChunkedUploadBackend
{
    /// <summary>
    /// Begins a multi-part upload.
    /// </summary>
    /// <param name="path">The destination.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>An opaque upload id the backend uses to correlate parts.</returns>
    Task<Result<string>> BeginChunkedAsync(BlobPath path, CancellationToken cancellationToken);

    /// <summary>
    /// Sends one part.
    /// </summary>
    /// <param name="path">The destination.</param>
    /// <param name="uploadId">The id from <see cref="BeginChunkedAsync"/>.</param>
    /// <param name="partNumber">Which part, from 1.</param>
    /// <param name="offset">Where this part starts in the finished object.</param>
    /// <param name="chunk">The bytes.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// A part tag the backend needs at completion, or an empty string when it
    /// needs none. S3 returns an ETag per part and requires them all back;
    /// SFTP needs nothing because the offset already placed the bytes.
    /// </returns>
    Task<Result<string>> AppendChunkAsync(
        BlobPath path,
        string uploadId,
        int partNumber,
        long offset,
        byte[] chunk,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finishes the upload.
    /// </summary>
    /// <param name="path">The destination.</param>
    /// <param name="uploadId">The id from <see cref="BeginChunkedAsync"/>.</param>
    /// <param name="partTags">The tags returned by each part, in order.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Success once the object is durable.</returns>
    Task<Result> CompleteChunkedAsync(
        BlobPath path,
        string uploadId,
        IReadOnlyList<string> partTags,
        CancellationToken cancellationToken);

    /// <summary>
    /// Abandons the upload and releases whatever the service is holding.
    /// </summary>
    /// <param name="path">The destination.</param>
    /// <param name="uploadId">The id from <see cref="BeginChunkedAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Success once abandoned.</returns>
    /// <remarks>
    /// Not optional politeness: an abandoned S3 multipart upload keeps its
    /// parts, and keeps billing for them, until a lifecycle rule removes it.
    /// </remarks>
    Task<Result> AbortChunkedAsync(BlobPath path, string uploadId, CancellationToken cancellationToken);
}

/// <summary>
/// A single-process upload accumulator.
/// </summary>
/// <remarks>
/// Superseded for large files by <c>ChunkedUploadService</c>, which tracks a
/// durable session and streams parts to the backend. This remains for the
/// small-payload case where buffering is genuinely cheaper than a session.
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
