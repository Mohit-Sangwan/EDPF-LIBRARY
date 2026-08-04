using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Compliance;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Storage;
using Edpf.Core.Guards;

namespace Edpf.Storage;

/// <summary>
/// Compression for stored blobs. Deflate via <see cref="GZipStream"/>, chosen
/// because it is in the base class library on every supported framework and
/// needs no package.
/// </summary>
/// <remarks>
/// Kept separate from the store so the ordering decision — compress, then
/// encrypt — lives in one readable place rather than inline in a long method.
/// </remarks>
public static class BlobCompression
{
    /// <summary>Compresses.</summary>
    /// <param name="plaintext">The bytes to compress.</param>
    /// <returns>The compressed bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plaintext"/> is null.</exception>
    public static byte[] Compress(byte[] plaintext)
    {
        Guard.NotNull(plaintext, nameof(plaintext));

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(plaintext, 0, plaintext.Length);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Decompresses, refusing anything that would expand past a declared bound.
    /// </summary>
    /// <param name="compressed">The compressed bytes.</param>
    /// <param name="maxLength">The largest plaintext this caller will accept.</param>
    /// <returns>The plaintext, or a failure.</returns>
    /// <remarks>
    /// **The bound is the point.** A few kilobytes of crafted gzip expands to
    /// gigabytes — the decompression-bomb attack — and a store that decompresses
    /// into an unbounded buffer hands an uploader the ability to exhaust the
    /// process's memory at will. The limit here is the same one the blob was
    /// written under, so a bomb cannot be created through this API at all; the
    /// check defends against one that arrived some other way.
    /// </remarks>
    public static Result<byte[]> Decompress(byte[] compressed, long maxLength)
    {
        Guard.NotNull(compressed, nameof(compressed));

        using var input = new MemoryStream(compressed, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();

        byte[] buffer = new byte[81920];
        long total = 0;

        while (true)
        {
            int read;
            try
            {
                read = gzip.Read(buffer, 0, buffer.Length);
            }
            catch (InvalidDataException)
            {
                return Result.Failure<byte[]>(new Error(
                    ErrorCodes.ProviderFailure,
                    "The stored blob is not valid compressed data.",
                    ErrorCategory.Internal));
            }

            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxLength)
            {
                return Result.Failure<byte[]>(new Error(
                    ErrorCodes.ValidationFailed,
                    "The stored blob expands beyond its declared maximum length.",
                    ErrorCategory.Validation));
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }
}

/// <summary>
/// Deletes blobs whose retention has lapsed, and refuses to delete anything
/// under legal hold.
/// </summary>
/// <remarks>
/// <para>
/// A retention schedule and a legal hold routinely disagree, and the resolution
/// is not a preference: **a hold outranks a schedule.** Deleting evidence on
/// schedule during litigation is spoliation, and "the retention job did it" has
/// never been a defence.
/// </para>
/// <para>
/// So the sweep asks the hold store first, per subject, and a blob whose
/// subject is on hold is skipped and counted rather than deleted. A blob with
/// no declared retention is never touched at all — a period nobody chose should
/// not start a clock.
/// </para>
/// </remarks>
public sealed class BlobLifecycleSweep
{
    private readonly TenantScopedBlobStore _store;
    private readonly ILegalHoldStore? _holds;
    private readonly IClock _clock;

    /// <summary>
    /// Composes a sweep.
    /// </summary>
    /// <param name="store">The store to sweep.</param>
    /// <param name="clock">Time source (Z.3 rule 4).</param>
    /// <param name="holds">
    /// The legal-hold store. Optional only for a deployment with no hold
    /// capability at all; when absent, blobs carrying a subject are skipped
    /// rather than deleted, because a hold that cannot be checked is not the
    /// same as a hold that is absent.
    /// </param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public BlobLifecycleSweep(TenantScopedBlobStore store, IClock clock, ILegalHoldStore? holds = null)
    {
        _store = Guard.NotNull(store, nameof(store));
        _clock = Guard.NotNull(clock, nameof(clock));
        _holds = holds;
    }

    /// <summary>
    /// Deletes every expired blob beneath a prefix in the current tenant.
    /// </summary>
    /// <param name="prefixSegments">Where to sweep.</param>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    /// <returns>What the sweep did.</returns>
    public async Task<Result<LifecycleOutcome>> RunAsync(
        IReadOnlyList<string> prefixSegments,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<BlobDescriptor>> listed =
            await _store.ListAsync(prefixSegments, cancellationToken).ConfigureAwait(false);

        if (listed.IsFailure)
        {
            return Result.Failure<LifecycleOutcome>(listed.Error!);
        }

        DateTimeOffset now = _clock.UtcNow;
        int deleted = 0;
        int heldBack = 0;
        int notDue = 0;

        foreach (BlobDescriptor descriptor in listed.Value)
        {
            if (descriptor.RetainUntilUtc is null || descriptor.RetainUntilUtc > now)
            {
                notDue++;
                continue;
            }

            Result<Guid?> subject = await _store
                .SubjectOfAsync(descriptor.Path, cancellationToken)
                .ConfigureAwait(false);

            if (subject.IsSuccess && subject.Value.HasValue)
            {
                if (_holds is null)
                {
                    // A hold that cannot be checked is not a hold that is
                    // absent. Skipping costs storage; deleting could cost a
                    // spoliation finding.
                    heldBack++;
                    continue;
                }

                LegalHold? hold = await _holds
                    .FindActiveHoldAsync(descriptor.Path.TenantId, subject.Value.Value.ToString("D"), cancellationToken)
                    .ConfigureAwait(false);

                if (hold is not null && hold.IsActive(now))
                {
                    heldBack++;
                    continue;
                }
            }

            Result removed = await _store.DeleteAsync(descriptor.Path, cancellationToken).ConfigureAwait(false);
            if (removed.IsSuccess)
            {
                deleted++;
            }
        }

        return new LifecycleOutcome(deleted, heldBack, notDue);
    }
}

/// <summary>What a lifecycle sweep did.</summary>
public sealed class LifecycleOutcome
{
    /// <summary>Records an outcome.</summary>
    /// <param name="deleted">How many blobs were deleted.</param>
    /// <param name="heldBack">How many were retained because of a legal hold.</param>
    /// <param name="notDue">How many were not yet due, or had no retention declared.</param>
    public LifecycleOutcome(int deleted, int heldBack, int notDue)
    {
        Deleted = deleted;
        HeldBack = heldBack;
        NotDue = notDue;
    }

    /// <summary>How many blobs were deleted.</summary>
    public int Deleted { get; }

    /// <summary>
    /// How many were retained because of a legal hold. Reported rather than
    /// silent: "the retention job ran and deleted nothing" and "the retention
    /// job ran and was blocked forty times" are different operational facts.
    /// </summary>
    public int HeldBack { get; }

    /// <summary>How many were not yet due, or carried no retention.</summary>
    public int NotDue { get; }
}

/// <summary>A chunked upload held in memory until completion.</summary>
/// <remarks>
/// Buffered rather than streamed to the backend, because the write-time
/// controls need the whole payload: a scanner shown one chunk cannot see a
/// signature straddling a boundary, and a hash is over the complete artefact.
/// The bound on that buffer is the caller's declared maximum, enforced per
/// chunk on arrival.
/// </remarks>
public sealed class BufferedUploadSession : IBlobUploadSession, IDisposable
{
    private readonly TenantScopedBlobStore _store;
    private readonly BlobPath _path;
    private readonly BlobWriteOptions _options;
    private readonly MemoryStream _buffer = new();
    private bool _aborted;

    internal BufferedUploadSession(
        TenantScopedBlobStore store, BlobPath path, BlobWriteOptions options, string sessionId)
    {
        _store = store;
        _path = path;
        _options = options;
        SessionId = sessionId;
    }

    /// <inheritdoc />
    public string SessionId { get; }

    /// <inheritdoc />
    public long BytesReceived => _buffer.Length;

    /// <inheritdoc />
    public async Task<Result> AppendAsync(byte[] chunk, CancellationToken cancellationToken)
    {
        Guard.NotNull(chunk, nameof(chunk));
        cancellationToken.ThrowIfCancellationRequested();

        if (_aborted)
        {
            return Result.Failure(new Error(
                ErrorCodes.ValidationFailed,
                "The upload session was aborted.",
                ErrorCategory.Validation));
        }

        if (_buffer.Length + chunk.Length > _options.MaxLength)
        {
            // Refused as it arrives. A session that accepts everything and
            // checks at completion has already paid the memory cost the limit
            // exists to avoid.
            _aborted = true;
            _buffer.SetLength(0);

            return Result.Failure(new Error(
                ErrorCodes.ValidationFailed,
                "The upload exceeds the declared maximum length.",
                ErrorCategory.Validation));
        }

        await _buffer.WriteAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<BlobDescriptor>> CompleteAsync(CancellationToken cancellationToken)
    {
        if (_aborted)
        {
            return Result.Failure<BlobDescriptor>(new Error(
                ErrorCodes.ValidationFailed,
                "The upload session was aborted.",
                ErrorCategory.Validation));
        }

        using var content = new MemoryStream(_buffer.ToArray(), writable: false);
        return await _store.WriteAsync(_path, content, _options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Abort()
    {
        _aborted = true;
        _buffer.SetLength(0);
    }

    /// <summary>Releases the buffer.</summary>
    /// <remarks>
    /// An abandoned session holds the whole payload in memory until it is
    /// disposed — which for a large study is the difference between a resumable
    /// upload and a leak.
    /// </remarks>
    public void Dispose() => _buffer.Dispose();
}

/// <summary>A scanner that finds nothing. Development and tests only.</summary>
/// <remarks>
/// Named so that its presence in a production composition is visible in a code
/// review. A deployment that wants no scanning should configure none and accept
/// <see cref="ScanState.NotScanned"/>, rather than install something that
/// reports every payload clean.
/// </remarks>
public sealed class NullContentScanner : IContentScanner
{
    /// <inheritdoc />
    public string ScannerName => "Null";

    /// <inheritdoc />
    public Task<Result<ScanVerdict>> ScanAsync(byte[] content, CancellationToken cancellationToken)
        => Task.FromResult(Result<ScanVerdict>.FromValue(ScanVerdict.Clean));
}

/// <summary>Formats an instant for sidecar metadata.</summary>
internal static class MetadataFormat
{
    internal static string Instant(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    internal static bool TryInstant(string? text, out DateTimeOffset value)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);
}
