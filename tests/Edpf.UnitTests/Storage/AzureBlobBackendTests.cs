using System.Net;
using System.Text;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Storage;
using Edpf.Storage.Remote;
using Edpf.UnitTests.TestDoubles;

namespace Edpf.UnitTests.Storage;

/// <summary>
/// The Azure Blob backend and its Shared Key signing, verified without a
/// subscription.
/// </summary>
public sealed class AzureBlobBackendTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    // A syntactically valid base64 key. Not a real credential and not usable
    // against anything — Z.10 forbids a real secret in a fixture.
    private const string AccountKey = "bXktdGVzdC1hY2NvdW50LWtleS1ub3QtcmVhbC0wMDAwMDAwMA==";

    private readonly FakeClock _clock = new();
    private readonly TestHashingService _hashing = new();

    // ── the signer ────────────────────────────────────────────────────────

    [Fact]
    public void StringToSign_HasThirteenFixedSlotsBeforeTheHeaders()
    {
        // The layout is positional. An omitted newline shifts everything after
        // it and the service answers only "signature did not match", so the
        // shape is asserted rather than assumed.
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["x-ms-date"] = "Wed, 05 Aug 2026 12:00:00 GMT",
        };

        string signed = AzureSharedKeySignature.StringToSign(
            "GET", 0, headers, "/account/container/blob");

        string[] lines = signed.Split('\n');

        Assert.Equal("GET", lines[0]);
        Assert.Equal("x-ms-date:Wed, 05 Aug 2026 12:00:00 GMT", lines[12]);
        Assert.Equal("/account/container/blob", lines[13]);
    }

    [Fact]
    public void StringToSign_LeavesContentLengthEmptyWhenZero()
    {
        // The trap. From API version 2015-02-21 the slot is the empty string
        // for a zero-length body, not "0" — and "0" is exactly what a careful
        // implementer writes.
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal);

        string empty = AzureSharedKeySignature.StringToSign("GET", 0, headers, "/a");
        string sized = AzureSharedKeySignature.StringToSign("PUT", 42, headers, "/a");

        Assert.Equal(string.Empty, empty.Split('\n')[3]);
        Assert.Equal("42", sized.Split('\n')[3]);
    }

    [Fact]
    public void CanonicalResource_PutsEachQueryParameterOnItsOwnLineSortedAndLowercased()
    {
        // Lower case is the wire format, not a normalisation preference.
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["restype"] = "container",
            ["COMP"] = "list",
        };

        string resource = AzureSharedKeySignature.CanonicalResource("acct", "/box", query);

        Assert.Equal("/acct/box\ncomp:list\nrestype:container", resource);
    }

    [Fact]
    public void AuthorizationHeader_SignsWithTheDecodedKeyNotTheBase64Text()
    {
        // The other classic error: HMAC-ing with the base64 *string* produces a
        // signature of exactly the right shape and the wrong value.
        var signer = new AzureSharedKeySignature(_hashing);
        var credentials = new AzureCredentials("acct", AccountKey);

        string header = signer.AuthorizationHeader(credentials, "GET\n\n\n\n\n\n\n\n\n\n\n\n/acct/x");

        string signature = header.Split(':')[1];
        byte[] expected = _hashing.HmacSha256(
            Convert.FromBase64String(AccountKey),
            Encoding.UTF8.GetBytes("GET\n\n\n\n\n\n\n\n\n\n\n\n/acct/x"));

        Assert.StartsWith("SharedKey acct:", header, StringComparison.Ordinal);
        Assert.Equal(Convert.ToBase64String(expected), signature);
    }

    [Fact]
    public void AuthorizationHeader_RejectsAKeyThatIsNotBase64()
    {
        var signer = new AzureSharedKeySignature(_hashing);

        Assert.Throws<FormatException>(
            () => signer.AuthorizationHeader(new AzureCredentials("acct", "not base64!"), "x"));
    }

    // ── the backend ───────────────────────────────────────────────────────

    private (AzureBlobBackend Backend, RecordingHandler Handler) CreateBackend()
    {
        var handler = new RecordingHandler();
        var http = new HttpClient(handler);

        return (new AzureBlobBackend(
            http,
            new Uri("https://acct.blob.core.windows.net"),
            "clinical",
            new AzureCredentials("acct", AccountKey),
            _hashing,
            _clock), handler);
    }

    [Fact]
    public async Task PutAsync_SendsABlockBlobPutWithTheRequiredHeaders()
    {
        (AzureBlobBackend backend, RecordingHandler handler) = CreateBackend();
        BlobPath path = BlobPath.Create(TenantA, "reports", "q3.bin");

        Assert.True((await backend.PutAsync(path, [1, 2, 3], default)).IsSuccess);

        HttpRequestMessage sent = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Put, sent.Method);
        Assert.Equal("/clinical/" + path.Value, sent.RequestUri!.AbsolutePath);
        Assert.Equal("BlockBlob", sent.Headers.GetValues("x-ms-blob-type").Single());
        Assert.Equal(AzureSharedKeySignature.ApiVersion, sent.Headers.GetValues("x-ms-version").Single());
        Assert.StartsWith("SharedKey acct:",
            sent.Headers.GetValues("Authorization").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_MapsNotFoundAndServerErrorDifferently()
    {
        // A storage outage must not look like data loss.
        (AzureBlobBackend backend, RecordingHandler handler) = CreateBackend();

        handler.Respond(HttpStatusCode.NotFound, []);
        Result<byte[]> missing = await backend.GetAsync(BlobPath.Create(TenantA, "a.bin"), default);

        handler.Respond(HttpStatusCode.ServiceUnavailable, []);
        Result<byte[]> down = await backend.GetAsync(BlobPath.Create(TenantA, "a.bin"), default);

        Assert.Equal(ErrorCodes.NotFound, missing.Error!.Code);
        Assert.Equal(ErrorCodes.ProviderFailure, down.Error!.Code);
        Assert.Equal(ErrorCategory.Transient, down.Error.Category);
    }

    [Fact]
    public async Task GetAsync_ReturnsTheBody()
    {
        (AzureBlobBackend backend, RecordingHandler handler) = CreateBackend();
        handler.Respond(HttpStatusCode.OK, [7, 7, 7]);

        Result<byte[]> read = await backend.GetAsync(BlobPath.Create(TenantA, "a.bin"), default);

        Assert.Equal([7, 7, 7], read.Value);
    }

    [Fact]
    public async Task MetadataRoundTrips_ThroughTheSidecarBlob()
    {
        (AzureBlobBackend backend, RecordingHandler handler) = CreateBackend();
        BlobPath path = BlobPath.Create(TenantA, "a.bin");

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["edpf.classification"] = "Phi",
            ["edpf.declared-content-type"] = "text/html; charset=utf-8",
        };

        await backend.PutMetadataAsync(path, metadata, default);

        Assert.EndsWith(".edpfmeta", handler.Requests[0].RequestUri!.AbsolutePath, StringComparison.Ordinal);

        handler.Respond(HttpStatusCode.OK, handler.Bodies[0]);
        Result<IReadOnlyDictionary<string, string>> read = await backend.GetMetadataAsync(path, default);

        Assert.True(read.IsSuccess);
        foreach (KeyValuePair<string, string> entry in metadata)
        {
            Assert.Equal(entry.Value, read.Value[entry.Key]);
        }
    }

    [Fact]
    public async Task ListAsync_ParsesBlobNamesAndExcludesSidecars()
    {
        (AzureBlobBackend backend, RecordingHandler handler) = CreateBackend();

        handler.Respond(HttpStatusCode.OK, Encoding.UTF8.GetBytes(
            """
            <?xml version="1.0" encoding="utf-8"?>
            <EnumerationResults>
              <Blobs>
                <Blob><Name>tenants/a/one.bin</Name></Blob>
                <Blob><Name>tenants/a/one.bin.edpfmeta</Name></Blob>
              </Blobs>
              <NextMarker />
            </EnumerationResults>
            """));

        Result<IReadOnlyList<string>> listed = await backend.ListAsync("tenants/a/", default);

        Assert.Equal("tenants/a/one.bin", Assert.Single(listed.Value));
    }

    [Fact]
    public async Task ListAsync_FollowsTheContinuationMarker()
    {
        // A container with more than 5,000 blobs pages. Ignoring the marker
        // returns the first page and reports it as the whole listing, which the
        // lifecycle sweep would then act on.
        (AzureBlobBackend backend, RecordingHandler handler) = CreateBackend();

        handler.RespondInSequence(
            Encoding.UTF8.GetBytes(
                """
                <EnumerationResults>
                  <Blobs><Blob><Name>tenants/a/one.bin</Name></Blob></Blobs>
                  <NextMarker>page2</NextMarker>
                </EnumerationResults>
                """),
            Encoding.UTF8.GetBytes(
                """
                <EnumerationResults>
                  <Blobs><Blob><Name>tenants/a/two.bin</Name></Blob></Blobs>
                  <NextMarker />
                </EnumerationResults>
                """));

        Result<IReadOnlyList<string>> listed = await backend.ListAsync("tenants/a/", default);

        Assert.Equal(2, listed.Value.Count);
        Assert.Contains("marker=page2", handler.Requests[1].RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListAsync_SignsTheQueryParametersItSends()
    {
        // The canonicalised resource must contain the same parameters as the
        // URL. Signing one set and sending another is a 403 that names nothing.
        (AzureBlobBackend backend, RecordingHandler handler) = CreateBackend();
        handler.Respond(HttpStatusCode.OK, Encoding.UTF8.GetBytes(
            "<EnumerationResults><Blobs /><NextMarker /></EnumerationResults>"));

        await backend.ListAsync("tenants/a/", default);

        string query = handler.Requests[0].RequestUri!.Query;

        Assert.Contains("comp=list", query, StringComparison.Ordinal);
        Assert.Contains("restype=container", query, StringComparison.Ordinal);
        Assert.Contains("prefix=", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryRequest_TakesItsDateFromTheClockSeam()
    {
        // Azure rejects a request whose date skews by more than fifteen
        // minutes. A backend reading the wall clock directly cannot be tested
        // for that without sleeping.
        (AzureBlobBackend backend, RecordingHandler handler) = CreateBackend();

        await backend.PutAsync(BlobPath.Create(TenantA, "a.bin"), [1], default);
        _clock.Advance(TimeSpan.FromHours(2));
        await backend.PutAsync(BlobPath.Create(TenantA, "b.bin"), [1], default);

        Assert.NotEqual(
            handler.Requests[0].Headers.GetValues("x-ms-date").Single(),
            handler.Requests[1].Headers.GetValues("x-ms-date").Single());
    }

    [Fact]
    public async Task RemoveAsync_OfAnAbsentBlob_IsNotFound()
    {
        (AzureBlobBackend backend, RecordingHandler handler) = CreateBackend();
        handler.Respond(HttpStatusCode.NotFound, []);

        Result removed = await backend.RemoveAsync(BlobPath.Create(TenantA, "missing.bin"), default);

        Assert.True(removed.IsFailure);
        Assert.Equal(ErrorCodes.NotFound, removed.Error!.Code);
    }

    /// <summary>Captures requests and replays canned responses. No network.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<byte[]> _sequence = new();
        private HttpStatusCode _status = HttpStatusCode.OK;
        private byte[] _body = [];

        public List<HttpRequestMessage> Requests { get; } = [];

        /// <summary>
        /// Bodies captured as they are sent, because the backend disposes each
        /// request once it is done — correctly.
        /// </summary>
        public List<byte[]> Bodies { get; } = [];

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

            return new HttpResponseMessage(_status) { Content = new ByteArrayContent(body) };
        }
    }
}
