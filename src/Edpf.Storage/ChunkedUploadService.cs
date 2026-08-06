using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Security;
using Edpf.Abstractions.Storage;
using Edpf.Abstractions.Tenancy;
using Edpf.Core.Guards;
using Edpf.Core.Time;

namespace Edpf.Storage;

/// <summary>Where an upload session has got to.</summary>
public enum UploadSessionStatus
{
    /// <summary>Accepting chunks.</summary>
    InProgress = 0,

    /// <summary>Every chunk received, hash verified, object committed.</summary>
    Completed = 1,

    /// <summary>Abandoned by the caller or expired.</summary>
    Aborted = 2,
}

/// <summary>
/// A durable, resumable upload.
/// </summary>
/// <remarks>
/// The session is the thing that makes an upload survive a dropped connection.
/// Without it a client that loses the network at 90% starts again, which for a
/// PACS study over hospital Wi-Fi is not an edge case — it is the normal case.
/// </remarks>
public sealed class UploadSession
{
    private readonly HashSet<int> _received = [];
    private readonly Dictionary<int, string> _chunkHashes = new();

    /// <summary>
    /// Opens a session.
    /// </summary>
    /// <param name="uploadId">The session id.</param>
    /// <param name="tenantId">The owning tenant.</param>
    /// <param name="path">The final destination.</param>
    /// <param name="totalSize">The declared total size in bytes.</param>
    /// <param name="chunkSize">The chunk size the client will use.</param>
    /// <param name="expectedHash">
    /// Lowercase hex SHA-256 of the whole file, declared up front by the
    /// client.
    /// </param>
    /// <param name="options">The write declarations applied at commit.</param>
    /// <param name="createdUtc">When the session opened.</param>
    /// <param name="expiresUtc">When it lapses.</param>
    /// <exception cref="ArgumentNullException">A reference argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The total or chunk size is not positive.</exception>
    public UploadSession(
        Guid uploadId,
        Guid tenantId,
        BlobPath path,
        long totalSize,
        int chunkSize,
        string expectedHash,
        BlobWriteOptions options,
        DateTimeOffset createdUtc,
        DateTimeOffset expiresUtc)
    {
        Path = Guard.NotNull(path, nameof(path));
        Options = Guard.NotNull(options, nameof(options));
        ExpectedHash = Guard.NotNullOrWhiteSpace(expectedHash, nameof(expectedHash));

        if (totalSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(totalSize));
        }

        if (chunkSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize));
        }

        UploadId = uploadId;
        TenantId = tenantId;
        TotalSize = totalSize;
        ChunkSize = chunkSize;
        CreatedUtc = createdUtc;
        ExpiresUtc = expiresUtc;
        Status = UploadSessionStatus.InProgress;

        // Ceiling division: a 25 MB file in 10 MB chunks is three chunks, the
        // last one short. Integer division would say two and lose the tail.
        TotalChunks = (int)((totalSize + chunkSize - 1) / chunkSize);
    }

    /// <summary>The session id.</summary>
    public Guid UploadId { get; }

    /// <summary>The owning tenant.</summary>
    public Guid TenantId { get; }

    /// <summary>The final destination.</summary>
    public BlobPath Path { get; }

    /// <summary>The declared total size in bytes.</summary>
    public long TotalSize { get; }

    /// <summary>The chunk size.</summary>
    public int ChunkSize { get; }

    /// <summary>How many chunks the file is divided into.</summary>
    public int TotalChunks { get; }

    /// <summary>The client's declared SHA-256 of the whole file.</summary>
    public string ExpectedHash { get; }

    /// <summary>The write declarations applied at commit.</summary>
    public BlobWriteOptions Options { get; }

    /// <summary>When the session opened.</summary>
    public DateTimeOffset CreatedUtc { get; }

    /// <summary>When it lapses.</summary>
    public DateTimeOffset ExpiresUtc { get; }

    /// <summary>When it completed, or null.</summary>
    public DateTimeOffset? CompletedUtc { get; private set; }

    /// <summary>Where it has got to.</summary>
    public UploadSessionStatus Status { get; private set; }

    /// <summary>The backend's upload id, when the backend is doing the assembly.</summary>
    public string? BackendUploadId { get; internal set; }

    /// <summary>Chunk numbers received, ascending.</summary>
    public IReadOnlyList<int> UploadedChunks
    {
        get
        {
            var ordered = new List<int>(_received);
            ordered.Sort();
            return ordered;
        }
    }

    /// <summary>
    /// Chunk numbers still outstanding, ascending.
    /// </summary>
    /// <remarks>
    /// The resume answer. A client that reconnects asks for this and sends only
    /// what is missing, rather than starting again — which is the entire point
    /// of the session.
    /// </remarks>
    public IReadOnlyList<int> MissingChunks
    {
        get
        {
            var missing = new List<int>();
            for (int i = 1; i <= TotalChunks; i++)
            {
                if (!_received.Contains(i))
                {
                    missing.Add(i);
                }
            }

            return missing;
        }
    }

    /// <summary>True when every chunk has arrived.</summary>
    public bool IsComplete => _received.Count == TotalChunks;

    /// <summary>The per-chunk hashes received, keyed by chunk number.</summary>
    public IReadOnlyDictionary<int, string> ChunkHashes => _chunkHashes;

    internal void RecordChunk(int chunkNumber, string chunkHash)
    {
        _received.Add(chunkNumber);
        _chunkHashes[chunkNumber] = chunkHash;
    }

    internal void MarkCompleted(DateTimeOffset completedUtc)
    {
        Status = UploadSessionStatus.Completed;
        CompletedUtc = completedUtc;
    }

    internal void MarkAborted() => Status = UploadSessionStatus.Aborted;

    internal bool HasExpired(DateTimeOffset now) => now >= ExpiresUtc;
}

/// <summary>Where upload sessions live between chunks.</summary>
/// <remarks>
/// A seam because a session must outlive the request that opened it and,
/// usually, the process. An in-memory implementation loses every in-flight
/// upload on a restart — which for a session whose whole purpose is surviving
/// interruption is worth stating rather than discovering.
/// </remarks>
public interface IUploadSessionStore
{
    /// <summary>Stores a new session.</summary>
    /// <param name="session">The session.</param>
    void Add(UploadSession session);

    /// <summary>Finds a session.</summary>
    /// <param name="uploadId">The session id.</param>
    /// <returns>The session, or null.</returns>
    UploadSession? Find(Guid uploadId);

    /// <summary>Removes a session.</summary>
    /// <param name="uploadId">The session id.</param>
    void Remove(Guid uploadId);
}

/// <summary>Holds sessions in process memory. Development and tests.</summary>
/// <remarks>
/// **Loses every in-flight upload on a restart.** Stated plainly, because a
/// session store that forgets is worse than no session store: the client
/// believes it can resume and cannot.
/// </remarks>
public sealed class InMemoryUploadSessionStore : IUploadSessionStore
{
    private readonly Dictionary<Guid, UploadSession> _sessions = [];

    /// <inheritdoc />
    public void Add(UploadSession session)
        => _sessions[Guard.NotNull(session, nameof(session)).UploadId] = session;

    /// <inheritdoc />
    public UploadSession? Find(Guid uploadId)
        => _sessions.TryGetValue(uploadId, out UploadSession? session) ? session : null;

    /// <inheritdoc />
    public void Remove(Guid uploadId) => _sessions.Remove(uploadId);
}

/// <summary>
/// Session-based chunked upload with resume, streaming each chunk to the
/// backend rather than buffering the file.
/// </summary>
/// <remarks>
/// <para>
/// This replaces the memory-buffered session for large files, and the
/// difference is not an optimisation. A PACS study, a scanned records bundle
/// or a theatre video is hundreds of megabytes; buffering one costs that much
/// RAM per concurrent upload, and a dropped connection at 90% throws all of it
/// away.
/// </para>
/// <para>
/// **Each provider assembles in the way it already knows how**, which is why
/// this delegates rather than merging itself:
/// </para>
/// <list type="bullet">
///   <item>S3 and MinIO — native multipart, assembled server-side.</item>
///   <item>Azure Blob — staged blocks committed as a list.</item>
///   <item>SFTP — writes at an explicit offset, so the file assembles in place
///     and a reconnecting client simply seeks. No temporary directory and no
///     merge pass, which is both faster and one fewer place to leave a
///     half-written file.</item>
/// </list>
/// <para>
/// **The end-to-end hash is the control that matters.** The client declares
/// SHA-256 of the whole file at initialisation; the service verifies it after
/// assembly. A chunk that arrived corrupted, twice, or out of order fails
/// there — before the object is visible to anything. For clinical imaging,
/// "the upload reported success" and "the bytes are the bytes the scanner
/// produced" are different claims, and only the hash establishes the second.
/// </para>
/// </remarks>
public sealed class ChunkedUploadService
{
    private readonly TenantScopedBlobStore _store;
    private readonly IBlobBackend _backend;
    private readonly IUploadSessionStore _sessions;
    private readonly IHashingService _hashing;
    private readonly IClock _clock;
    private readonly ITenantContextAccessor _tenantAccessor;

    /// <summary>
    /// Composes the service.
    /// </summary>
    /// <param name="store">The policy layer, used to commit the finished object.</param>
    /// <param name="backend">The backend. Streams parts when it implements <see cref="IChunkedUploadBackend"/>.</param>
    /// <param name="sessions">Where sessions live between chunks.</param>
    /// <param name="hashing">Hashing seam (Z.10).</param>
    /// <param name="tenantAccessor">Ambient tenant.</param>
    /// <param name="clock">Time source (Z.3 rule 4).</param>
    /// <exception cref="ArgumentNullException">Any dependency is null.</exception>
    public ChunkedUploadService(
        TenantScopedBlobStore store,
        IBlobBackend backend,
        IUploadSessionStore sessions,
        IHashingService hashing,
        ITenantContextAccessor tenantAccessor,
        IClock clock)
    {
        _store = Guard.NotNull(store, nameof(store));
        _backend = Guard.NotNull(backend, nameof(backend));
        _sessions = Guard.NotNull(sessions, nameof(sessions));
        _hashing = Guard.NotNull(hashing, nameof(hashing));
        _tenantAccessor = Guard.NotNull(tenantAccessor, nameof(tenantAccessor));
        _clock = Guard.NotNull(clock, nameof(clock));
    }

    /// <summary>How long a session stays open without activity.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>True when the backend assembles parts itself rather than buffering.</summary>
    public bool BackendStreams => _backend is IChunkedUploadBackend;

    /// <summary>
    /// Opens a session.
    /// </summary>
    /// <param name="path">The final destination.</param>
    /// <param name="totalSize">The declared total size.</param>
    /// <param name="chunkSize">The chunk size the client will use.</param>
    /// <param name="expectedHash">Lowercase hex SHA-256 of the whole file.</param>
    /// <param name="options">Write declarations applied at commit.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The session, or a failure.</returns>
    public async Task<Result<UploadSession>> InitializeAsync(
        BlobPath path,
        long totalSize,
        int chunkSize,
        string expectedHash,
        BlobWriteOptions options,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));
        Guard.NotNull(options, nameof(options));
        Guard.NotNullOrWhiteSpace(expectedHash, nameof(expectedHash));

        ITenantContext? tenant = _tenantAccessor.Current;
        if (tenant is null || !BlobPath.BelongsTo(path, tenant.TenantId))
        {
            return Result.Failure<UploadSession>(NotFound());
        }

        if (totalSize > options.MaxLength)
        {
            // Refused up front rather than at the last chunk. A client should
            // learn its file is too large before spending twenty minutes
            // sending it.
            return Result.Failure<UploadSession>(new Error(
                ErrorCodes.ValidationFailed,
                "The declared total size exceeds the maximum this upload permits.",
                ErrorCategory.Validation));
        }

        DateTimeOffset now = StorableInstant.Normalize(_clock.UtcNow);

        var session = new UploadSession(
            Guid.NewGuid(),
            tenant.TenantId,
            path,
            totalSize,
            chunkSize,
            expectedHash,
            options,
            now,
            now.Add(SessionLifetime));

        if (_backend is IChunkedUploadBackend chunked)
        {
            Result<string> begun = await chunked
                .BeginChunkedAsync(path, cancellationToken)
                .ConfigureAwait(false);

            if (begun.IsFailure)
            {
                return Result.Failure<UploadSession>(begun.Error!);
            }

            session.BackendUploadId = begun.Value;
        }

        _sessions.Add(session);
        return session;
    }

    /// <summary>
    /// Accepts one chunk.
    /// </summary>
    /// <param name="uploadId">The session.</param>
    /// <param name="chunkNumber">Which chunk, from 1.</param>
    /// <param name="chunk">The bytes.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The session, or a failure.</returns>
    /// <remarks>
    /// Idempotent: re-sending a chunk after a timeout is safe, which matters
    /// because a client that timed out cannot know whether the server received
    /// it.
    /// </remarks>
    public async Task<Result<UploadSession>> UploadChunkAsync(
        Guid uploadId,
        int chunkNumber,
        byte[] chunk,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(chunk, nameof(chunk));

        Result<UploadSession> found = Resolve(uploadId);
        if (found.IsFailure)
        {
            return found;
        }

        UploadSession session = found.Value;

        if (chunkNumber < 1 || chunkNumber > session.TotalChunks)
        {
            return Result.Failure<UploadSession>(new Error(
                ErrorCodes.ValidationFailed,
                "That chunk number is outside the range this session declared.",
                ErrorCategory.Validation));
        }

        // Every chunk but the last must be exactly the declared size. A short
        // chunk in the middle would leave a hole that the final hash catches —
        // but catching it here names the chunk, and catching it at the hash
        // only says "the file is wrong".
        bool isLast = chunkNumber == session.TotalChunks;
        long expected = isLast
            ? session.TotalSize - ((long)(session.TotalChunks - 1) * session.ChunkSize)
            : session.ChunkSize;

        if (chunk.Length != expected)
        {
            return Result.Failure<UploadSession>(new Error(
                ErrorCodes.ValidationFailed,
                "Chunk " + chunkNumber.ToString(CultureInfo.InvariantCulture)
                + " is not the size this session declared for it.",
                ErrorCategory.Validation));
        }

        long offset = (long)(chunkNumber - 1) * session.ChunkSize;

        if (_backend is IChunkedUploadBackend chunked && session.BackendUploadId is not null)
        {
            Result<string> sent = await chunked.AppendChunkAsync(
                session.Path, session.BackendUploadId, chunkNumber, offset, chunk, cancellationToken)
                .ConfigureAwait(false);

            if (sent.IsFailure)
            {
                return Result.Failure<UploadSession>(sent.Error!);
            }

            session.RecordChunk(chunkNumber, sent.Value);
        }
        else
        {
            // Fallback for a backend that cannot assemble: each chunk is a
            // separate object, merged at commit. Slower and it costs a second
            // pass, but it is correct and it is bounded — nothing holds the
            // whole file.
            Result staged = await StageChunkAsync(session, chunkNumber, chunk, cancellationToken)
                .ConfigureAwait(false);

            if (staged.IsFailure)
            {
                return Result.Failure<UploadSession>(staged.Error!);
            }

            session.RecordChunk(chunkNumber, ToHex(_hashing.Sha256(chunk)));
        }

        return session;
    }

    /// <summary>
    /// Reports what has arrived and what has not.
    /// </summary>
    /// <param name="uploadId">The session.</param>
    /// <returns>The session, or a failure.</returns>
    public Result<UploadSession> Status(Guid uploadId) => Resolve(uploadId);

    /// <summary>
    /// Finishes the upload: verifies every chunk arrived, assembles, checks the
    /// hash, and commits through the policy layer.
    /// </summary>
    /// <param name="uploadId">The session.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The stored blob's descriptor, or a failure.</returns>
    public async Task<Result<BlobDescriptor>> CompleteAsync(
        Guid uploadId,
        CancellationToken cancellationToken)
    {
        Result<UploadSession> found = Resolve(uploadId);
        if (found.IsFailure)
        {
            return Result.Failure<BlobDescriptor>(found.Error!);
        }

        UploadSession session = found.Value;

        if (!session.IsComplete)
        {
            // Names how many are outstanding so a client can act, without
            // listing them all in a message that may be very long.
            return Result.Failure<BlobDescriptor>(new Error(
                ErrorCodes.ValidationFailed,
                session.MissingChunks.Count.ToString(CultureInfo.InvariantCulture)
                + " chunks are still outstanding; query the session status for which.",
                ErrorCategory.Validation));
        }

        Result<byte[]> assembled = await AssembleAsync(session, cancellationToken).ConfigureAwait(false);
        if (assembled.IsFailure)
        {
            return Result.Failure<BlobDescriptor>(assembled.Error!);
        }

        // The end-to-end check. A chunk that arrived corrupted, duplicated or
        // out of order fails here, before the object is visible to anything.
        // "The upload reported success" and "the bytes are the bytes the
        // scanner produced" are different claims.
        string actual = ToHex(_hashing.Sha256(assembled.Value));

        if (!string.Equals(actual, session.ExpectedHash, StringComparison.OrdinalIgnoreCase))
        {
            await AbortAsync(uploadId, cancellationToken).ConfigureAwait(false);

            return Result.Failure<BlobDescriptor>(new Error(
                ErrorCodes.ValidationFailed,
                "The assembled file does not match the hash the client declared; the upload was discarded.",
                ErrorCategory.Validation));
        }

        // Committed through the store, so scanning, encryption, compression,
        // versioning and retention all apply exactly as they would to a
        // single-request write. A chunked upload is not a way around the
        // controls.
        using var content = new System.IO.MemoryStream(assembled.Value, writable: false);

        Result<BlobDescriptor> written = await _store
            .WriteAsync(session.Path, content, session.Options, cancellationToken)
            .ConfigureAwait(false);

        if (written.IsFailure)
        {
            return written;
        }

        session.MarkCompleted(StorableInstant.Normalize(_clock.UtcNow));
        await CleanUpStagingAsync(session, cancellationToken).ConfigureAwait(false);

        return written;
    }

    /// <summary>Abandons a session and releases what the backend is holding.</summary>
    /// <param name="uploadId">The session.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Success once abandoned.</returns>
    public async Task<Result> AbortAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        Result<UploadSession> found = Resolve(uploadId);
        if (found.IsFailure)
        {
            return Result.Failure(found.Error!);
        }

        UploadSession session = found.Value;

        if (_backend is IChunkedUploadBackend chunked && session.BackendUploadId is not null)
        {
            // Not politeness: an abandoned S3 multipart upload keeps its parts,
            // and keeps billing for them, until a lifecycle rule notices.
            await chunked.AbortChunkedAsync(session.Path, session.BackendUploadId, cancellationToken)
                .ConfigureAwait(false);
        }

        await CleanUpStagingAsync(session, cancellationToken).ConfigureAwait(false);

        session.MarkAborted();
        _sessions.Remove(uploadId);

        return Result.Success();
    }

    private async Task<Result<byte[]>> AssembleAsync(
        UploadSession session,
        CancellationToken cancellationToken)
    {
        if (_backend is IChunkedUploadBackend chunked && session.BackendUploadId is not null)
        {
            var tags = new List<string>(session.TotalChunks);
            for (int i = 1; i <= session.TotalChunks; i++)
            {
                tags.Add(session.ChunkHashes[i]);
            }

            Result completed = await chunked.CompleteChunkedAsync(
                session.Path, session.BackendUploadId, tags, cancellationToken).ConfigureAwait(false);

            if (completed.IsFailure)
            {
                return Result.Failure<byte[]>(completed.Error!);
            }

            // Read back to verify the hash. The provider assembled it; that it
            // assembled the right thing is not something to take on trust for
            // a clinical image.
            return await _backend.GetAsync(session.Path, cancellationToken).ConfigureAwait(false);
        }

        using var buffer = new System.IO.MemoryStream();

        for (int i = 1; i <= session.TotalChunks; i++)
        {
            Result<BlobPath> stagePath = StagingPathFor(session, i);
            if (stagePath.IsFailure)
            {
                return Result.Failure<byte[]>(stagePath.Error!);
            }

            Result<byte[]> part = await _backend
                .GetAsync(stagePath.Value, cancellationToken)
                .ConfigureAwait(false);

            if (part.IsFailure)
            {
                return Result.Failure<byte[]>(part.Error!);
            }

            await buffer.WriteAsync(part.Value.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }

    private async Task<Result> StageChunkAsync(
        UploadSession session,
        int chunkNumber,
        byte[] chunk,
        CancellationToken cancellationToken)
    {
        Result<BlobPath> stagePath = StagingPathFor(session, chunkNumber);

        return stagePath.IsFailure
            ? Result.Failure(stagePath.Error!)
            : await _backend.PutAsync(stagePath.Value, chunk, cancellationToken).ConfigureAwait(false);
    }

    private async Task CleanUpStagingAsync(UploadSession session, CancellationToken cancellationToken)
    {
        if (_backend is IChunkedUploadBackend)
        {
            return;
        }

        for (int i = 1; i <= session.TotalChunks; i++)
        {
            Result<BlobPath> stagePath = StagingPathFor(session, i);
            if (stagePath.IsSuccess)
            {
                await _backend.RemoveAsync(stagePath.Value, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static Result<BlobPath> StagingPathFor(UploadSession session, int chunkNumber)
    {
        try
        {
            return BlobPath.Create(
                session.TenantId,
                "__uploads",
                session.UploadId.ToString("N"),
                "chunk_" + chunkNumber.ToString("D6", CultureInfo.InvariantCulture));
        }
        catch (ArgumentException)
        {
            return Result.Failure<BlobPath>(NotFound());
        }
    }

    private Result<UploadSession> Resolve(Guid uploadId)
    {
        ITenantContext? tenant = _tenantAccessor.Current;
        UploadSession? session = _sessions.Find(uploadId);

        // A session belonging to another tenant is indistinguishable from one
        // that does not exist, so upload ids cannot be probed across tenants.
        if (tenant is null || session is null || session.TenantId != tenant.TenantId)
        {
            return Result.Failure<UploadSession>(NotFound());
        }

        if (session.Status != UploadSessionStatus.InProgress)
        {
            return Result.Failure<UploadSession>(new Error(
                ErrorCodes.ValidationFailed,
                "That upload session is no longer in progress.",
                ErrorCategory.Validation));
        }

        if (session.HasExpired(_clock.UtcNow))
        {
            return Result.Failure<UploadSession>(new Error(
                ErrorCodes.ValidationFailed,
                "That upload session has expired.",
                ErrorCategory.Validation));
        }

        return session;
    }

    private static string ToHex(byte[] digest)
    {
        const string HexDigits = "0123456789abcdef";
        var chars = new char[digest.Length * 2];

        for (int i = 0; i < digest.Length; i++)
        {
            chars[i * 2] = HexDigits[digest[i] >> 4];
            chars[(i * 2) + 1] = HexDigits[digest[i] & 0x0F];
        }

        return new string(chars);
    }

    private static Error NotFound() => new(
        ErrorCodes.NotFound,
        "The requested resource was not found.",
        ErrorCategory.NotFound);
}
