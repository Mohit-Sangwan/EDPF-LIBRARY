using System.Net;
using System.Text;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Storage;
using Edpf.Storage.Remote;
using Edpf.UnitTests.TestDoubles;

namespace Edpf.UnitTests.Storage;

/// <summary>
/// The Azure Files backend. Every test here is about one of the three ways the
/// Files protocol differs from Blob — which is why it is a separate adapter
/// rather than a different URL.
/// </summary>
public sealed class AzureFilesBackendTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private const string AccountKey = "bXktdGVzdC1hY2NvdW50LWtleS1ub3QtcmVhbC0wMDAwMDAwMA==";

    private readonly FakeClock _clock = new();
    private readonly TestHashingService _hashing = new();

    private (AzureFilesBackend Backend, RecordingHandler Handler) CreateBackend()
    {
        var handler = new RecordingHandler();
        var http = new HttpClient(handler);

        return (new AzureFilesBackend(
            http,
            new Uri("https://acct.file.core.windows.net"),
            "clinical",
            new AzureCredentials("acct", AccountKey),
            _hashing,
            _clock), handler);
    }

    // ── difference 1: allocate, then fill ─────────────────────────────────

    [Fact]
    public async Task PutAsync_AllocatesAtFinalLengthThenWritesTheRange()
    {
        // There is no single-request upload. A PUT with x-ms-content-length
        // allocates; a second PUT ?comp=range fills it.
        (AzureFilesBackend backend, RecordingHandler handler) = CreateBackend();
        BlobPath path = BlobPath.Create(TenantA, "q3.bin");

        Assert.True((await backend.PutAsync(path, [1, 2, 3, 4, 5], default)).IsSuccess);

        // Two directory creations (tenants, tenants/{guid}), then create, then write.
        HttpRequestMessage create = handler.Requests[^2];
        HttpRequestMessage write = handler.Requests[^1];

        Assert.Equal("file", create.Headers.GetValues("x-ms-type").Single());
        Assert.Equal("5", create.Headers.GetValues("x-ms-content-length").Single());
        Assert.Empty(handler.Bodies[^2]);

        Assert.Contains("comp=range", write.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Equal("update", write.Headers.GetValues("x-ms-write").Single());
        Assert.Equal("bytes=0-4", write.Headers.GetValues("Range").Single());
        Assert.Equal<byte[]>([1, 2, 3, 4, 5], handler.Bodies[^1]);
    }

    [Fact]
    public async Task PutAsync_OfAnEmptyFile_DoesNotSendARangedWrite()
    {
        // A ranged write of zero bytes is rejected by the service, not ignored.
        // The allocation alone fully describes an empty file.
        (AzureFilesBackend backend, RecordingHandler handler) = CreateBackend();

        Assert.True((await backend.PutAsync(BlobPath.Create(TenantA, "empty.bin"), [], default)).IsSuccess);

        Assert.DoesNotContain(
            handler.Requests, r => r.RequestUri!.Query.Contains("comp=range", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RangedWrite_SignsTheRangeHeader()
    {
        // The Range slot sits eleventh in the string to sign. Blob never fills
        // it and Files always does, so a shared signer that omitted it would
        // work for one service and 403 for the other.
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal);

        string withRange = AzureSharedKeySignature.StringToSign(
            "PUT", 5, headers, "/acct/share/f", "bytes=0-4");

        Assert.Equal("bytes=0-4", withRange.Split('\n')[11]);

        (AzureFilesBackend backend, RecordingHandler handler) = CreateBackend();
        await backend.PutAsync(BlobPath.Create(TenantA, "q3.bin"), [1, 2], default);

        Assert.True(handler.Requests[^1].Headers.Contains("Range"));
    }

    // ── difference 2: directories are real ────────────────────────────────

    [Fact]
    public async Task PutAsync_CreatesEveryAncestorDirectoryFirst()
    {
        // Blob storage has no directories — a slash is a character in a key.
        // Here, writing into a share with no ancestor directory fails.
        (AzureFilesBackend backend, RecordingHandler handler) = CreateBackend();
        BlobPath path = BlobPath.Create(TenantA, "docs", "reports", "q3.bin");

        await backend.PutAsync(path, [1], default);

        List<string> directoryPaths = handler.Requests
            .Where(r => r.RequestUri!.Query.Contains("restype=directory", StringComparison.Ordinal))
            .Select(r => r.RequestUri!.AbsolutePath)
            .ToList();

        Assert.Equal(
            [
                "/clinical/tenants",
                "/clinical/tenants/" + TenantA.ToString("D"),
                "/clinical/tenants/" + TenantA.ToString("D") + "/docs",
                "/clinical/tenants/" + TenantA.ToString("D") + "/docs/reports",
            ],
            directoryPaths);
    }

    [Fact]
    public async Task DirectoryThatAlreadyExists_IsNotAFailure()
    {
        // 409 Conflict is the common case, not an error: the directory is
        // there, which is all this step wanted.
        //
        // Scoped to the directory requests only. An earlier version of this
        // test answered 409 to everything, which also failed the file
        // creation — the assertion was wrong, not the backend, and a 409 on
        // creating the *file* really is a failure.
        (AzureFilesBackend backend, RecordingHandler handler) = CreateBackend();
        handler.StatusFor = request =>
            request.RequestUri!.Query.Contains("restype=directory", StringComparison.Ordinal)
                ? HttpStatusCode.Conflict
                : HttpStatusCode.Created;

        Result written = await backend.PutAsync(BlobPath.Create(TenantA, "q3.bin"), [], default);

        Assert.True(written.IsSuccess);
    }

    [Fact]
    public async Task ConflictOnTheFileItself_IsAFailure()
    {
        // The other half of the same rule. A 409 creating the file is not the
        // benign "already there" case, and treating it as one would report a
        // write that never happened as a success.
        (AzureFilesBackend backend, RecordingHandler handler) = CreateBackend();
        handler.StatusFor = _ => HttpStatusCode.Conflict;

        Result written = await backend.PutAsync(BlobPath.Create(TenantA, "q3.bin"), [1], default);

        Assert.True(written.IsFailure);
    }

    // ── difference 3: listing means walking ───────────────────────────────

    [Fact]
    public async Task ListAsync_WalksSubdirectoriesAndExcludesSidecars()
    {
        // Files lists per directory and has no prefix query, so enumerating a
        // subtree means recursing into it.
        (AzureFilesBackend backend, RecordingHandler handler) = CreateBackend();

        handler.RespondInSequence(
            Encoding.UTF8.GetBytes(
                """
                <EnumerationResults>
                  <Entries>
                    <File><Name>one.bin</Name></File>
                    <File><Name>one.bin.edpfmeta</Name></File>
                    <Directory><Name>nested</Name></Directory>
                  </Entries>
                </EnumerationResults>
                """),
            Encoding.UTF8.GetBytes(
                """
                <EnumerationResults>
                  <Entries><File><Name>two.bin</Name></File></Entries>
                </EnumerationResults>
                """));

        Result<IReadOnlyList<string>> listed = await backend.ListAsync("tenants/a/docs/", default);

        Assert.True(listed.IsSuccess);
        Assert.Equal(["tenants/a/docs/one.bin", "tenants/a/docs/nested/two.bin"], listed.Value);
    }

    [Fact]
    public async Task ListAsync_OfAMissingDirectory_IsEmptyRatherThanAFailure()
    {
        // "No documents yet" and "storage is down" have to stay
        // distinguishable, so a 404 on the root of the walk is an empty list.
        (AzureFilesBackend backend, RecordingHandler handler) = CreateBackend();
        handler.Respond(HttpStatusCode.NotFound, []);

        Result<IReadOnlyList<string>> listed = await backend.ListAsync("tenants/a/nothing/", default);

        Assert.True(listed.IsSuccess);
        Assert.Empty(listed.Value);
    }

    [Fact]
    public async Task ListAsync_WhenTheServiceFails_IsAFailureNotAnEmptyList()
    {
        (AzureFilesBackend backend, RecordingHandler handler) = CreateBackend();
        handler.Respond(HttpStatusCode.ServiceUnavailable, []);

        Result<IReadOnlyList<string>> listed = await backend.ListAsync("tenants/a/", default);

        Assert.True(listed.IsFailure);
        Assert.Equal(ErrorCodes.ProviderFailure, listed.Error!.Code);
    }

    // ── the shared contract ───────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_MapsNotFoundAndServerErrorDifferently()
    {
        (AzureFilesBackend backend, RecordingHandler handler) = CreateBackend();

        handler.Respond(HttpStatusCode.NotFound, []);
        Result<byte[]> missing = await backend.GetAsync(BlobPath.Create(TenantA, "a.bin"), default);

        handler.Respond(HttpStatusCode.ServiceUnavailable, []);
        Result<byte[]> down = await backend.GetAsync(BlobPath.Create(TenantA, "a.bin"), default);

        Assert.Equal(ErrorCodes.NotFound, missing.Error!.Code);
        Assert.Equal(ErrorCodes.ProviderFailure, down.Error!.Code);
    }

    [Fact]
    public async Task MetadataRoundTripsThroughASidecarFile()
    {
        (AzureFilesBackend backend, RecordingHandler handler) = CreateBackend();
        BlobPath path = BlobPath.Create(TenantA, "a.bin");

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["edpf.classification"] = "Phi",
            ["edpf.declared-content-type"] = "text/html; charset=utf-8",
        };

        await backend.PutMetadataAsync(path, metadata, default);

        Assert.EndsWith(".edpfmeta", handler.Requests[^1].RequestUri!.AbsolutePath, StringComparison.Ordinal);

        handler.Respond(HttpStatusCode.OK, handler.Bodies[^1]);
        Result<IReadOnlyDictionary<string, string>> read = await backend.GetMetadataAsync(path, default);

        Assert.True(read.IsSuccess);
        foreach (KeyValuePair<string, string> entry in metadata)
        {
            Assert.Equal(entry.Value, read.Value[entry.Key]);
        }
    }

    [Fact]
    public async Task EveryRequest_IsSignedAndVersioned()
    {
        (AzureFilesBackend backend, RecordingHandler handler) = CreateBackend();

        await backend.PutAsync(BlobPath.Create(TenantA, "a.bin"), [1], default);

        Assert.All(handler.Requests, request =>
        {
            Assert.StartsWith("SharedKey acct:",
                request.Headers.GetValues("Authorization").Single(), StringComparison.Ordinal);
            Assert.Equal(AzureSharedKeySignature.ApiVersion,
                request.Headers.GetValues("x-ms-version").Single());
        });
    }

    /// <summary>Captures requests and replays canned responses. No network.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<byte[]> _sequence = new();
        private HttpStatusCode _status = HttpStatusCode.OK;
        private byte[] _body = [];

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<byte[]> Bodies { get; } = [];

        /// <summary>Chooses a status per request, when a test needs to distinguish them.</summary>
        public Func<HttpRequestMessage, HttpStatusCode>? StatusFor { get; set; }

        public void Respond(HttpStatusCode status, byte[] body)
        {
            _status = status;
            _body = body;
        }

        public void RespondInSequence(params byte[][] bodies)
        {
            foreach (byte[] body in bodies)
            {
                _sequence.Enqueue(body);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken));

            byte[] body = _sequence.Count > 0 ? _sequence.Dequeue() : _body;
            HttpStatusCode status = StatusFor?.Invoke(request) ?? _status;

            return new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        }
    }
}
