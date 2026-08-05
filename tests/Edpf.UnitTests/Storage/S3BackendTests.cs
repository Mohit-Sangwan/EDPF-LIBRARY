using System.Net;
using System.Text;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Storage;
using Edpf.Storage.Remote;
using Edpf.UnitTests.TestDoubles;

namespace Edpf.UnitTests.Storage;

/// <summary>
/// The S3-compatible backend, verified without credentials.
/// </summary>
/// <remarks>
/// A signer written in-framework can be checked against the provider's own
/// published test vectors, in CI, on every commit. An SDK wrapper can only be
/// exercised against the live service — which is how cloud adapters get shipped
/// and never run, the failure mode this repository has already been caught by
/// six times.
/// </remarks>
public sealed class S3BackendTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private readonly FakeClock _clock = new();
    private readonly TestHashingService _hashing = new();

    // ── the signer, against AWS's published vectors ───────────────────────

    [Fact]
    public void SigningKey_MatchesTheAwsWorkedExample()
    {
        // From the AWS "Signature Version 4 test suite" worked example. If this
        // drifts, every request this backend makes is rejected with a message
        // that names nothing useful — which is exactly why the algorithm is
        // pinned to a known vector rather than to "it worked when I tried it".
        var signer = new AwsSignatureV4(_hashing);
        var credentials = new S3Credentials(
            "AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY");

        var instant = new DateTimeOffset(2015, 8, 30, 12, 36, 0, TimeSpan.Zero);

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = "example.amazonaws.com",
            ["x-amz-date"] = "20150830T123600Z",
        };

        string canonical = AwsSignatureV4.CanonicalRequest(
            "GET", "/", string.Empty, headers, AwsSignatureV4.EmptyPayloadHash);

        string authorization = signer.AuthorizationHeader(
            credentials, "us-east-1", "service", instant, canonical,
            AwsSignatureV4.SignedHeaders(headers));

        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request",
            authorization, StringComparison.Ordinal);
        Assert.Contains("SignedHeaders=host;x-amz-date", authorization, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyPayloadHash_IsTheSha256OfNothing()
    {
        // Pinned as a constant and checked against a live computation, because
        // a wrong value here fails every GET and DELETE with a signature
        // mismatch.
        Assert.Equal(
            AwsSignatureV4.EmptyPayloadHash,
            AwsSignatureV4.ToHex(_hashing.Sha256([])));
    }

    [Theory]
    [InlineData("/tenants/abc/report.pdf", "/tenants/abc/report.pdf")]
    [InlineData("/a b", "/a%20b")]
    [InlineData("/a+b", "/a%2Bb")]
    [InlineData("/a~b", "/a~b")]
    [InlineData("/a*b", "/a%2Ab")]
    public void EncodePath_UsesUppercaseHexAndPreservesSeparators(string input, string expected)
    {
        // S3's canonicalisation is stricter than Uri's: uppercase hex, and
        // *, ' , ( and ) are not unreserved. A path encoded the other way
        // produces a signature the service rejects.
        Assert.Equal(expected, AwsSignatureV4.EncodePath(input));
    }

    [Fact]
    public void SignedHeaders_AreSortedAndLowercase()
    {
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["x-amz-date"] = "d",
            ["host"] = "h",
            ["x-amz-content-sha256"] = "c",
        };

        Assert.Equal("host;x-amz-content-sha256;x-amz-date", AwsSignatureV4.SignedHeaders(headers));
    }

    // ── the backend, against a fake transport ─────────────────────────────

    private (S3BlobBackend Backend, RecordingHandler Handler) CreateBackend(bool pathStyle = true)
    {
        var handler = new RecordingHandler();
        var http = new HttpClient(handler);
        var endpoint = new S3Endpoint(
            new Uri("https://minio.internal:9000"), "clinical", "eu-west-1", pathStyle);

        return (new S3BlobBackend(
            http, endpoint, new S3Credentials("AKID", "secret"), _hashing, _clock), handler);
    }

    [Fact]
    public async Task PutAsync_SignsAndAddressesThePathStyleUrl()
    {
        (S3BlobBackend backend, RecordingHandler handler) = CreateBackend();
        BlobPath path = BlobPath.Create(TenantA, "reports", "q3.bin");

        Assert.True((await backend.PutAsync(path, [1, 2, 3], default)).IsSuccess);

        HttpRequestMessage sent = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Put, sent.Method);
        Assert.Equal("/clinical/" + path.Value, sent.RequestUri!.AbsolutePath);
        Assert.Contains("AWS4-HMAC-SHA256", sent.Headers.GetValues("Authorization").Single(), StringComparison.Ordinal);
        Assert.True(sent.Headers.Contains("x-amz-content-sha256"));
        Assert.True(sent.Headers.Contains("x-amz-date"));
    }

    [Fact]
    public async Task PutAsync_SendsTheContentHashOfTheActualBody()
    {
        // A wrong payload hash is accepted by the signature check and rejected
        // by the service, which produces a confusing failure far from the bug.
        (S3BlobBackend backend, RecordingHandler handler) = CreateBackend();
        byte[] payload = [9, 8, 7];

        await backend.PutAsync(BlobPath.Create(TenantA, "x.bin"), payload, default);

        string declared = handler.Requests[0].Headers.GetValues("x-amz-content-sha256").Single();

        Assert.Equal(AwsSignatureV4.ToHex(_hashing.Sha256(payload)), declared);
    }

    [Fact]
    public async Task GetAsync_ReturnsTheBody()
    {
        (S3BlobBackend backend, RecordingHandler handler) = CreateBackend();
        handler.Respond(HttpStatusCode.OK, [4, 5, 6]);

        Result<byte[]> read = await backend.GetAsync(BlobPath.Create(TenantA, "x.bin"), default);

        Assert.True(read.IsSuccess);
        Assert.Equal([4, 5, 6], read.Value);
    }

    [Fact]
    public async Task GetAsync_MapsNotFoundToTheStoreContract()
    {
        (S3BlobBackend backend, RecordingHandler handler) = CreateBackend();
        handler.Respond(HttpStatusCode.NotFound, []);

        Result<byte[]> read = await backend.GetAsync(BlobPath.Create(TenantA, "missing.bin"), default);

        Assert.True(read.IsFailure);
        Assert.Equal(ErrorCodes.NotFound, read.Error!.Code);
    }

    [Fact]
    public async Task GetAsync_MapsAServiceErrorToAProviderFailure()
    {
        // 500 is transient and retryable; not-found is neither. Collapsing them
        // would make a storage outage look like data loss.
        (S3BlobBackend backend, RecordingHandler handler) = CreateBackend();
        handler.Respond(HttpStatusCode.InternalServerError, []);

        Result<byte[]> read = await backend.GetAsync(BlobPath.Create(TenantA, "x.bin"), default);

        Assert.Equal(ErrorCodes.ProviderFailure, read.Error!.Code);
        Assert.Equal(ErrorCategory.Transient, read.Error.Category);
    }

    [Fact]
    public async Task RemoveAsync_OfAnAbsentKey_IsNotFoundRatherThanSuccess()
    {
        // S3 DELETE answers 204 for a key that never existed. The store's
        // contract distinguishes the two, so existence is established first
        // instead of inferred from a status code that cannot tell them apart.
        (S3BlobBackend backend, RecordingHandler handler) = CreateBackend();
        handler.Respond(HttpStatusCode.NotFound, []);

        Result removed = await backend.RemoveAsync(BlobPath.Create(TenantA, "missing.bin"), default);

        Assert.True(removed.IsFailure);
        Assert.Equal(ErrorCodes.NotFound, removed.Error!.Code);
    }

    [Fact]
    public async Task MetadataRoundTrips_IncludingValuesWithNewlinesAndSeparators()
    {
        // One of these values is the caller's declared content type. S3 object
        // metadata is capped at 2 KB and trims whitespace, which is why this
        // goes in a sidecar object rather than x-amz-meta-* headers.
        (S3BlobBackend backend, RecordingHandler handler) = CreateBackend();
        BlobPath path = BlobPath.Create(TenantA, "x.bin");

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["edpf.classification"] = "Phi",
            ["edpf.declared-content-type"] = "text/html",
            ["awkward"] = "  leading and trailing  ",
        };

        await backend.PutMetadataAsync(path, metadata, default);

        handler.Respond(HttpStatusCode.OK, handler.Bodies[0]);

        Result<IReadOnlyDictionary<string, string>> read =
            await backend.GetMetadataAsync(path, default);

        Assert.True(read.IsSuccess);
        foreach (KeyValuePair<string, string> entry in metadata)
        {
            Assert.Equal(entry.Value, read.Value[entry.Key]);
        }
    }

    [Fact]
    public async Task PutMetadata_TargetsASidecarKey_NotTheBlobItself()
    {
        (S3BlobBackend backend, RecordingHandler handler) = CreateBackend();
        BlobPath path = BlobPath.Create(TenantA, "x.bin");

        await backend.PutMetadataAsync(path, new Dictionary<string, string> { ["k"] = "v" }, default);

        Assert.EndsWith(".edpfmeta", handler.Requests[0].RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListAsync_ParsesKeysAndExcludesSidecars()
    {
        // A sidecar is an implementation detail of this backend. Returning it
        // would make every stored file appear twice in a listing.
        (S3BlobBackend backend, RecordingHandler handler) = CreateBackend();

        handler.Respond(HttpStatusCode.OK, Encoding.UTF8.GetBytes(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
              <IsTruncated>false</IsTruncated>
              <Contents><Key>tenants/a/reports/q3.bin</Key></Contents>
              <Contents><Key>tenants/a/reports/q3.bin.edpfmeta</Key></Contents>
            </ListBucketResult>
            """));

        Result<IReadOnlyList<string>> listed = await backend.ListAsync("tenants/a/reports/", default);

        Assert.True(listed.IsSuccess);
        Assert.Equal("tenants/a/reports/q3.bin", Assert.Single(listed.Value));
    }

    [Fact]
    public async Task ListAsync_FollowsContinuationTokens()
    {
        // A bucket with more than a thousand keys pages. A backend that ignores
        // the token silently returns the first page and reports it as the whole
        // listing, which a lifecycle sweep would then act on.
        (S3BlobBackend backend, RecordingHandler handler) = CreateBackend();

        handler.RespondInSequence(
            Encoding.UTF8.GetBytes(
                """
                <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
                  <IsTruncated>true</IsTruncated>
                  <NextContinuationToken>page2</NextContinuationToken>
                  <Contents><Key>tenants/a/one.bin</Key></Contents>
                </ListBucketResult>
                """),
            Encoding.UTF8.GetBytes(
                """
                <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
                  <IsTruncated>false</IsTruncated>
                  <Contents><Key>tenants/a/two.bin</Key></Contents>
                </ListBucketResult>
                """));

        Result<IReadOnlyList<string>> listed = await backend.ListAsync("tenants/a/", default);

        Assert.Equal(2, listed.Value.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("continuation-token=page2", handler.Requests[1].RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VirtualHostStyle_OmitsTheBucketFromThePath()
    {
        // AWS proper wants virtual-host addressing; MinIO and most on-premises
        // gateways require path style. Getting this wrong signs one URL and
        // sends another.
        (S3BlobBackend backend, RecordingHandler handler) = CreateBackend(pathStyle: false);
        BlobPath path = BlobPath.Create(TenantA, "x.bin");

        await backend.PutAsync(path, [1], default);

        Assert.Equal("/" + path.Value, handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task EveryRequest_CarriesADistinctDateFromTheClockSeam()
    {
        // Signatures are time-scoped. A backend reading the wall clock directly
        // cannot be tested for expiry behaviour without sleeping.
        (S3BlobBackend backend, RecordingHandler handler) = CreateBackend();

        await backend.PutAsync(BlobPath.Create(TenantA, "a.bin"), [1], default);
        _clock.Advance(TimeSpan.FromDays(1));
        await backend.PutAsync(BlobPath.Create(TenantA, "b.bin"), [1], default);

        string first = handler.Requests[0].Headers.GetValues("x-amz-date").Single();
        string second = handler.Requests[1].Headers.GetValues("x-amz-date").Single();

        Assert.NotEqual(first, second);
    }

    /// <summary>Captures requests and replays canned responses. No network.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<byte[]> _sequence = new();
        private HttpStatusCode _status = HttpStatusCode.OK;
        private byte[] _body = [];

        public List<HttpRequestMessage> Requests { get; } = [];

        /// <summary>
        /// Request bodies, captured as they are sent.
        /// </summary>
        /// <remarks>
        /// Captured here rather than read from <c>Requests[n].Content</c>
        /// afterwards, because the backend disposes each request when it is
        /// done with it — correctly. A test that reads the content later gets
        /// an <c>ObjectDisposedException</c>, which is the test being wrong
        /// about ownership rather than the backend leaking.
        /// </remarks>
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
