using System.Text;
using Edpf.Abstractions.Metadata;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Storage;
using Edpf.Abstractions.Tenancy;
using Edpf.Core.Tenancy;
using Edpf.Metadata;
using Edpf.Storage;
using Edpf.UnitTests.TestDoubles;

namespace Edpf.UnitTests.Storage;

/// <summary>
/// Session-based chunked upload: the capability that makes a PACS study
/// survive hospital Wi-Fi.
/// </summary>
public sealed class ChunkedUploadTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private readonly InMemoryBlobBackend _backend = new();
    private readonly TenantContextAccessor _tenants = new();
    private readonly ReversibleTestCryptoProvider _crypto = new();
    private readonly TestHashingService _hashing = new();
    private readonly InMemoryUploadSessionStore _sessions = new();
    private readonly FakeClock _clock = new();

    private TenantScopedBlobStore CreateStore()
        => new(_backend, _tenants, ProtectionPolicy.Default, _hashing, _clock, _crypto);

    private ChunkedUploadService CreateService()
        => new(CreateStore(), _backend, _sessions, _hashing, _tenants, _clock);

    private IDisposable ActAs(Guid tenantId)
        => _tenants.Push(new TenantDescriptor(
            tenantId, "tenant", "eu-west", TenantIsolationMode.SharedSchema, Guid.NewGuid()));

    private static BlobWriteOptions Options(
        DataClassificationLevel classification = DataClassificationLevel.Public)
        => new(classification, "application/dicom", 100_000_000);

    private string HashOf(byte[] bytes)
    {
        const string Digits = "0123456789abcdef";
        byte[] digest = _hashing.Sha256(bytes);
        var chars = new char[digest.Length * 2];

        for (int i = 0; i < digest.Length; i++)
        {
            chars[i * 2] = Digits[digest[i] >> 4];
            chars[(i * 2) + 1] = Digits[digest[i] & 0x0F];
        }

        return new string(chars);
    }

    private static byte[] Slice(byte[] all, int chunkNumber, int chunkSize)
    {
        int offset = (chunkNumber - 1) * chunkSize;
        int length = Math.Min(chunkSize, all.Length - offset);
        return all.AsSpan(offset, length).ToArray();
    }

    // ── the session ───────────────────────────────────────────────────────

    [Fact]
    public async Task Initialize_DividesTheFileAndRoundsTheTailUp()
    {
        // 25 bytes in 10-byte chunks is three, the last one short. Integer
        // division says two and loses the tail.
        ChunkedUploadService service = CreateService();

        using (ActAs(TenantA))
        {
            UploadSession session = (await service.InitializeAsync(
                BlobPath.Create(TenantA, "study.dcm"), 25, 10, "abc", Options(), default)).Value;

            Assert.Equal(3, session.TotalChunks);
            Assert.Equal([1, 2, 3], session.MissingChunks);
            Assert.Empty(session.UploadedChunks);
        }
    }

    [Fact]
    public async Task Initialize_RefusesAFileLargerThanTheDeclaredMaximum()
    {
        // Up front, not at the last chunk. A client should learn its file is
        // too large before spending twenty minutes sending it.
        ChunkedUploadService service = CreateService();
        var options = new BlobWriteOptions(DataClassificationLevel.Public, "application/dicom", 100);

        using (ActAs(TenantA))
        {
            Result<UploadSession> initialised = await service.InitializeAsync(
                BlobPath.Create(TenantA, "study.dcm"), 1_000, 10, "abc", options, default);

            Assert.True(initialised.IsFailure);
        }
    }

    [Fact]
    public async Task Initialize_ForAnotherTenantsPath_IsRefused()
    {
        ChunkedUploadService service = CreateService();

        using (ActAs(TenantA))
        {
            Assert.True((await service.InitializeAsync(
                BlobPath.Create(TenantB, "study.dcm"), 10, 10, "abc", Options(), default)).IsFailure);
        }
    }

    // ── resume ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_ReportsExactlyWhichChunksAreMissing()
    {
        // The resume answer. A client that reconnects sends only what is
        // missing rather than starting again, which is the entire point.
        byte[] file = Encoding.UTF8.GetBytes(new string('x', 25));
        ChunkedUploadService service = CreateService();

        using (ActAs(TenantA))
        {
            UploadSession session = (await service.InitializeAsync(
                BlobPath.Create(TenantA, "study.dcm"), 25, 10, HashOf(file), Options(), default)).Value;

            await service.UploadChunkAsync(session.UploadId, 1, Slice(file, 1, 10), default);
            await service.UploadChunkAsync(session.UploadId, 3, Slice(file, 3, 10), default);

            UploadSession status = service.Status(session.UploadId).Value;

            Assert.Equal([1, 3], status.UploadedChunks);
            Assert.Equal([2], status.MissingChunks);
        }
    }

    [Fact]
    public async Task UploadChunk_IsIdempotent()
    {
        // A client that timed out cannot know whether the server received the
        // chunk, so re-sending has to be safe.
        byte[] file = Encoding.UTF8.GetBytes(new string('x', 20));
        ChunkedUploadService service = CreateService();

        using (ActAs(TenantA))
        {
            UploadSession session = (await service.InitializeAsync(
                BlobPath.Create(TenantA, "study.dcm"), 20, 10, HashOf(file), Options(), default)).Value;

            await service.UploadChunkAsync(session.UploadId, 1, Slice(file, 1, 10), default);
            await service.UploadChunkAsync(session.UploadId, 1, Slice(file, 1, 10), default);

            Assert.Equal([1], service.Status(session.UploadId).Value.UploadedChunks);
        }
    }

    [Fact]
    public async Task Complete_WhileChunksAreOutstanding_IsRefused()
    {
        byte[] file = Encoding.UTF8.GetBytes(new string('x', 25));
        ChunkedUploadService service = CreateService();

        using (ActAs(TenantA))
        {
            UploadSession session = (await service.InitializeAsync(
                BlobPath.Create(TenantA, "study.dcm"), 25, 10, HashOf(file), Options(), default)).Value;

            await service.UploadChunkAsync(session.UploadId, 1, Slice(file, 1, 10), default);

            Result<BlobDescriptor> completed = await service.CompleteAsync(session.UploadId, default);

            Assert.True(completed.IsFailure);
            Assert.Contains("2 chunks", completed.Error!.Message, StringComparison.Ordinal);
        }
    }

    // ── the chunk sizing contract ─────────────────────────────────────────

    [Fact]
    public async Task UploadChunk_OfTheWrongSize_IsRefusedAndNamesTheChunk()
    {
        // A short chunk in the middle leaves a hole. The final hash catches
        // it, but catching it here names which chunk — the hash only says the
        // file is wrong.
        byte[] file = Encoding.UTF8.GetBytes(new string('x', 25));
        ChunkedUploadService service = CreateService();

        using (ActAs(TenantA))
        {
            UploadSession session = (await service.InitializeAsync(
                BlobPath.Create(TenantA, "study.dcm"), 25, 10, HashOf(file), Options(), default)).Value;

            Result<UploadSession> short1 = await service.UploadChunkAsync(
                session.UploadId, 1, new byte[3], default);

            Assert.True(short1.IsFailure);
            Assert.Contains("Chunk 1", short1.Error!.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task UploadChunk_AcceptsAShortFinalChunk()
    {
        byte[] file = Encoding.UTF8.GetBytes(new string('x', 25));
        ChunkedUploadService service = CreateService();

        using (ActAs(TenantA))
        {
            UploadSession session = (await service.InitializeAsync(
                BlobPath.Create(TenantA, "study.dcm"), 25, 10, HashOf(file), Options(), default)).Value;

            // The third chunk is five bytes, not ten.
            Assert.True((await service.UploadChunkAsync(
                session.UploadId, 3, Slice(file, 3, 10), default)).IsSuccess);
        }
    }

    [Fact]
    public async Task UploadChunk_OutsideTheDeclaredRange_IsRefused()
    {
        ChunkedUploadService service = CreateService();

        using (ActAs(TenantA))
        {
            UploadSession session = (await service.InitializeAsync(
                BlobPath.Create(TenantA, "study.dcm"), 20, 10, "abc", Options(), default)).Value;

            Assert.True((await service.UploadChunkAsync(session.UploadId, 0, new byte[10], default)).IsFailure);
            Assert.True((await service.UploadChunkAsync(session.UploadId, 9, new byte[10], default)).IsFailure);
        }
    }

    // ── the end-to-end hash ───────────────────────────────────────────────

    [Fact]
    public async Task Complete_AssemblesInOrderAndCommits()
    {
        byte[] file = Encoding.UTF8.GetBytes("HEADERxxxxBODYyyyyTAILzzz");
        ChunkedUploadService service = CreateService();
        BlobPath path = BlobPath.Create(TenantA, "study.dcm");

        using (ActAs(TenantA))
        {
            UploadSession session = (await service.InitializeAsync(
                path, file.Length, 10, HashOf(file), Options(), default)).Value;

            // Deliberately out of order, as a parallel client would send them.
            await service.UploadChunkAsync(session.UploadId, 3, Slice(file, 3, 10), default);
            await service.UploadChunkAsync(session.UploadId, 1, Slice(file, 1, 10), default);
            await service.UploadChunkAsync(session.UploadId, 2, Slice(file, 2, 10), default);

            Result<BlobDescriptor> completed = await service.CompleteAsync(session.UploadId, default);

            Assert.True(completed.IsSuccess);
            Assert.Equal(file.Length, completed.Value.Length);

            using BlobContent stored = (await CreateStore().ReadAsync(path, default)).Value;
            using var reader = new StreamReader(stored.Content);

            Assert.Equal("HEADERxxxxBODYyyyyTAILzzz", await reader.ReadToEndAsync());
        }
    }

    [Fact]
    public async Task Complete_WithAMismatchedHash_DiscardsTheUpload()
    {
        // "The upload reported success" and "the bytes are the bytes the
        // scanner produced" are different claims. Only the hash establishes
        // the second, and a corrupted chunk fails here before the object is
        // visible to anything.
        byte[] file = Encoding.UTF8.GetBytes(new string('x', 20));
        ChunkedUploadService service = CreateService();
        BlobPath path = BlobPath.Create(TenantA, "study.dcm");

        using (ActAs(TenantA))
        {
            UploadSession session = (await service.InitializeAsync(
                path, 20, 10, HashOf(Encoding.UTF8.GetBytes(new string('y', 20))), Options(), default)).Value;

            await service.UploadChunkAsync(session.UploadId, 1, Slice(file, 1, 10), default);
            await service.UploadChunkAsync(session.UploadId, 2, Slice(file, 2, 10), default);

            Result<BlobDescriptor> completed = await service.CompleteAsync(session.UploadId, default);

            Assert.True(completed.IsFailure);
            Assert.Contains("hash", completed.Error!.Message, StringComparison.OrdinalIgnoreCase);
        }

        // Nothing was committed.
        Assert.Null(_backend.RawBytesAt(path.Value));
    }

    // ── a chunked upload is not a way around the controls ─────────────────

    [Fact]
    public async Task Complete_AppliesEncryptionJustLikeASingleRequestWrite()
    {
        byte[] file = Encoding.UTF8.GetBytes("MRN-000123 clinical detail");
        ChunkedUploadService service = CreateService();
        BlobPath path = BlobPath.Create(TenantA, "study.dcm");

        using (ActAs(TenantA))
        {
            UploadSession session = (await service.InitializeAsync(
                path, file.Length, 100, HashOf(file),
                Options(DataClassificationLevel.Phi), default)).Value;

            await service.UploadChunkAsync(session.UploadId, 1, file, default);

            Result<BlobDescriptor> completed = await service.CompleteAsync(session.UploadId, default);

            Assert.True(completed.Value.IsEncryptedAtRest);
        }

        Assert.DoesNotContain(
            "MRN-000123",
            Encoding.UTF8.GetString(_backend.RawBytesAt(path.Value)!),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Complete_RemovesTheStagedChunks()
    {
        // Otherwise every upload leaves its parts behind, and a PACS archive
        // doubles in size for no reason.
        byte[] file = Encoding.UTF8.GetBytes(new string('x', 20));
        ChunkedUploadService service = CreateService();

        using (ActAs(TenantA))
        {
            UploadSession session = (await service.InitializeAsync(
                BlobPath.Create(TenantA, "study.dcm"), 20, 10, HashOf(file), Options(), default)).Value;

            await service.UploadChunkAsync(session.UploadId, 1, Slice(file, 1, 10), default);
            await service.UploadChunkAsync(session.UploadId, 2, Slice(file, 2, 10), default);
            await service.CompleteAsync(session.UploadId, default);

            IReadOnlyList<BlobDescriptor> staged =
                (await CreateStore().ListAsync(["__uploads"], default)).Value;

            Assert.Empty(staged);
        }
    }

    // ── sessions expire, and belong to one tenant ─────────────────────────

    [Fact]
    public async Task Session_OfAnotherTenant_IsIndistinguishableFromMissing()
    {
        ChunkedUploadService service = CreateService();
        Guid uploadId;

        using (ActAs(TenantA))
        {
            uploadId = (await service.InitializeAsync(
                BlobPath.Create(TenantA, "study.dcm"), 20, 10, "abc", Options(), default)).Value.UploadId;
        }

        using (ActAs(TenantB))
        {
            Result<UploadSession> probed = service.Status(uploadId);

            Assert.True(probed.IsFailure);
            Assert.Equal(ErrorCodes.NotFound, probed.Error!.Code);
        }
    }

    [Fact]
    public async Task Session_ThatHasExpired_IsRefused()
    {
        ChunkedUploadService service = CreateService();
        service.SessionLifetime = TimeSpan.FromMinutes(30);

        using (ActAs(TenantA))
        {
            UploadSession session = (await service.InitializeAsync(
                BlobPath.Create(TenantA, "study.dcm"), 20, 10, "abc", Options(), default)).Value;

            _clock.Advance(TimeSpan.FromHours(2));

            Assert.True(service.Status(session.UploadId).IsFailure);
        }
    }

    [Fact]
    public async Task Abort_ReleasesTheSessionAndItsStagedChunks()
    {
        byte[] file = Encoding.UTF8.GetBytes(new string('x', 20));
        ChunkedUploadService service = CreateService();

        using (ActAs(TenantA))
        {
            UploadSession session = (await service.InitializeAsync(
                BlobPath.Create(TenantA, "study.dcm"), 20, 10, HashOf(file), Options(), default)).Value;

            await service.UploadChunkAsync(session.UploadId, 1, Slice(file, 1, 10), default);
            Assert.True((await service.AbortAsync(session.UploadId, default)).IsSuccess);

            Assert.True(service.Status(session.UploadId).IsFailure);
            Assert.Empty((await CreateStore().ListAsync(["__uploads"], default)).Value);
        }
    }

    [Fact]
    public void BackendStreams_ReportsWhetherTheBackendAssemblesItself()
    {
        // So a deployment can find out which of its backends actually stream,
        // rather than discovering it from a memory graph.
        Assert.False(CreateService().BackendStreams);
    }
}
