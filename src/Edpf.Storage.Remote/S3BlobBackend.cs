using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Security;
using Edpf.Abstractions.Storage;
using Edpf.Core.Guards;

namespace Edpf.Storage.Remote;

/// <summary>Where an S3-compatible endpoint lives and how to address it.</summary>
public sealed class S3Endpoint
{
    /// <summary>
    /// Describes an endpoint.
    /// </summary>
    /// <param name="serviceUri">
    /// The service root — <c>https://s3.eu-west-1.amazonaws.com</c> for AWS,
    /// <c>https://minio.internal:9000</c> for MinIO, or the regional endpoint
    /// for Oracle Object Storage or any other S3-compatible service.
    /// </param>
    /// <param name="bucket">The bucket.</param>
    /// <param name="region">The signing region.</param>
    /// <param name="usePathStyle">
    /// Path-style addressing (<c>host/bucket/key</c>) rather than virtual-host
    /// style. Required by MinIO and most on-premises gateways, and the reason
    /// this is a parameter rather than a constant.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="serviceUri"/> is null.</exception>
    /// <exception cref="ArgumentException">The bucket or region is blank.</exception>
    public S3Endpoint(Uri serviceUri, string bucket, string region, bool usePathStyle = true)
    {
        ServiceUri = Guard.NotNull(serviceUri, nameof(serviceUri));
        Bucket = Guard.NotNullOrWhiteSpace(bucket, nameof(bucket));
        Region = Guard.NotNullOrWhiteSpace(region, nameof(region));
        UsePathStyle = usePathStyle;
    }

    /// <summary>The service root.</summary>
    public Uri ServiceUri { get; }

    /// <summary>The bucket.</summary>
    public string Bucket { get; }

    /// <summary>The signing region.</summary>
    public string Region { get; }

    /// <summary>Whether to use path-style addressing.</summary>
    public bool UsePathStyle { get; }
}

/// <summary>
/// An <see cref="IBlobBackend"/> over any S3-compatible object store.
/// </summary>
/// <remarks>
/// <para>
/// **One adapter, five of the named backends.** AWS S3, MinIO, Oracle Object
/// Storage, Wasabi, Ceph RADOS Gateway and Google Cloud Storage's XML API all
/// speak this protocol; they differ by endpoint URI, region string and whether
/// path-style addressing is required. Shipping five adapters that wrap five
/// SDKs to reach one protocol would be five times the supply chain and five
/// times the surface, for no additional capability.
/// </para>
/// <para>
/// No AWS SDK. The transport is <see cref="HttpClient"/> and the signing is
/// <see cref="AwsSignatureV4"/>, which is what makes this **testable without
/// credentials** — a fake message handler asserts the exact signed request.
/// An SDK wrapper can only be exercised against the real service, which is how
/// cloud adapters end up shipped and never run.
/// </para>
/// <para>
/// It inherits every control in <c>TenantScopedBlobStore</c> — tenancy,
/// encryption, scanning, versioning, retention — because it is a backend, and
/// backends do I/O. Server-side encryption is deliberately *not* configured
/// here: the payload arriving at this class is already ciphertext whenever the
/// classification requires it, and relying on the provider's encryption instead
/// would move the key out of the deployment's control.
/// </para>
/// </remarks>
public sealed class S3BlobBackend : IBlobBackend, IChunkedUploadBackend
{
    private const string Service = "s3";

    private readonly HttpClient _http;
    private readonly S3Endpoint _endpoint;
    private readonly S3Credentials _credentials;
    private readonly AwsSignatureV4 _signer;
    private readonly IClock _clock;

    /// <summary>
    /// Composes the backend.
    /// </summary>
    /// <param name="http">The transport. Its lifetime belongs to the caller.</param>
    /// <param name="endpoint">Which service and bucket.</param>
    /// <param name="credentials">Signing credentials, resolved from a secret store.</param>
    /// <param name="hashing">The hashing seam (Z.10).</param>
    /// <param name="clock">Time source. Signatures are time-scoped, so this is not incidental.</param>
    /// <exception cref="ArgumentNullException">Any dependency is null.</exception>
    public S3BlobBackend(
        HttpClient http,
        S3Endpoint endpoint,
        S3Credentials credentials,
        IHashingService hashing,
        IClock clock)
    {
        _http = Guard.NotNull(http, nameof(http));
        _endpoint = Guard.NotNull(endpoint, nameof(endpoint));
        _credentials = Guard.NotNull(credentials, nameof(credentials));
        _clock = Guard.NotNull(clock, nameof(clock));
        _signer = new AwsSignatureV4(Guard.NotNull(hashing, nameof(hashing)));
    }

    /// <inheritdoc />
    public string BackendName => "S3";

    /// <inheritdoc />
    public async Task<Result> PutAsync(BlobPath path, byte[] bytes, CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));
        Guard.NotNull(bytes, nameof(bytes));

        using HttpRequestMessage request = Sign(HttpMethod.Put, KeyFor(path), bytes);
        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<byte[]>> GetAsync(BlobPath path, CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));

        using HttpRequestMessage request = Sign(HttpMethod.Get, KeyFor(path), Array.Empty<byte>());
        using HttpResponseMessage response = await _http
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Result.Failure<byte[]>(NotFound());
        }

        if (!response.IsSuccessStatusCode)
        {
            return Result.Failure<byte[]>(ProviderFailure());
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result> RemoveAsync(BlobPath path, CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));

        // S3 DELETE is idempotent and answers 204 for a key that was never
        // there. The store's contract says a missing blob is a not-found, so
        // existence is established first rather than inferred from a status
        // code that cannot distinguish the two.
        Result<IReadOnlyDictionary<string, string>> existing =
            await GetMetadataAsync(path, cancellationToken).ConfigureAwait(false);

        if (existing.IsFailure)
        {
            return Result.Failure(existing.Error!);
        }

        using HttpRequestMessage request = Sign(HttpMethod.Delete, KeyFor(path), Array.Empty<byte>());
        Result deleted = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (deleted.IsFailure)
        {
            return deleted;
        }

        using HttpRequestMessage sidecar = Sign(
            HttpMethod.Delete, KeyFor(path) + MetadataSuffix, Array.Empty<byte>());

        await SendAsync(sidecar, cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<string>>> ListAsync(
        string renderedPrefix,
        CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(renderedPrefix, nameof(renderedPrefix));

        var keys = new List<string>();
        string? continuation = null;

        do
        {
            string query = "list-type=2&prefix=" + Uri.EscapeDataString(renderedPrefix);
            if (continuation is not null)
            {
                query += "&continuation-token=" + Uri.EscapeDataString(continuation);
            }

            using HttpRequestMessage request = Sign(
                HttpMethod.Get, string.Empty, Array.Empty<byte>(), CanonicalQuery(query));

            using HttpResponseMessage response = await _http
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Result.Failure<IReadOnlyList<string>>(ProviderFailure());
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            continuation = ParseListing(body, keys);
        }
        while (continuation is not null);

        return keys;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyDictionary<string, string>>> GetMetadataAsync(
        BlobPath path,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));

        using HttpRequestMessage request = Sign(
            HttpMethod.Get, KeyFor(path) + MetadataSuffix, Array.Empty<byte>());

        using HttpResponseMessage response = await _http
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Result.Failure<IReadOnlyDictionary<string, string>>(NotFound());
        }

        if (!response.IsSuccessStatusCode)
        {
            return Result.Failure<IReadOnlyDictionary<string, string>>(ProviderFailure());
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in body.Split('\n'))
        {
            int separator = line.IndexOf(Separator, StringComparison.Ordinal);
            if (separator > 0)
            {
                metadata[line.Substring(0, separator)] = line.Substring(separator + 1);
            }
        }

        return metadata;
    }

    /// <inheritdoc />
    public async Task<Result> PutMetadataAsync(
        BlobPath path,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));
        Guard.NotNull(metadata, nameof(metadata));

        // Stored as a sidecar object rather than as x-amz-meta-* headers.
        // Object metadata is capped at 2 KB, is silently trimmed of whitespace,
        // and cannot be changed without rewriting the object — none of which is
        // acceptable for a record that carries the blob's classification.
        var builder = new StringBuilder();
        foreach (KeyValuePair<string, string> entry in metadata)
        {
            builder.Append(entry.Key).Append(Separator).Append(entry.Value).Append('\n');
        }

        byte[] payload = Encoding.UTF8.GetBytes(builder.ToString());

        using HttpRequestMessage request = Sign(HttpMethod.Put, KeyFor(path) + MetadataSuffix, payload);
        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Separates key from value in a sidecar line. A unit separator cannot occur in either.</summary>
    private const char Separator = '\u001F';

    /// <summary>The sidecar key suffix. Reserved.</summary>
    internal const string MetadataSuffix = ".edpfmeta";

    /// <inheritdoc />
    public async Task<Result<string>> BeginChunkedAsync(BlobPath path, CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));

        using HttpRequestMessage request = Sign(
            HttpMethod.Post, KeyFor(path), Array.Empty<byte>(), "uploads=");

        using HttpResponseMessage response = await _http
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return Result.Failure<string>(ProviderFailure());
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        XDocument document = XDocument.Parse(body);
        XNamespace ns = document.Root?.Name.Namespace ?? XNamespace.None;

        string? uploadId = document.Descendants(ns + "UploadId").FirstOrDefault()?.Value;

        return uploadId is null ? Result.Failure<string>(ProviderFailure()) : uploadId;
    }

    /// <inheritdoc />
    public async Task<Result<string>> AppendChunkAsync(
        BlobPath path,
        string uploadId,
        int partNumber,
        long offset,
        byte[] chunk,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));
        Guard.NotNullOrWhiteSpace(uploadId, nameof(uploadId));
        Guard.NotNull(chunk, nameof(chunk));

        // The offset is ignored here and that is correct for S3: a part's
        // position in the finished object is its part NUMBER, not a byte
        // offset. Honouring the offset instead would silently reorder a
        // resumed upload.
        string query = CanonicalQuery(
            "partNumber=" + partNumber.ToString(CultureInfo.InvariantCulture)
            + "&uploadId=" + Uri.EscapeDataString(uploadId));

        using HttpRequestMessage request = Sign(HttpMethod.Put, KeyFor(path), chunk, query);

        using HttpResponseMessage response = await _http
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return Result.Failure<string>(ProviderFailure());
        }

        // The ETag is required back at completion. Losing it means the upload
        // cannot be finished and the parts sit there billing until a lifecycle
        // rule notices.
        string? tag = response.Headers.ETag?.Tag;

        return tag is null ? Result.Failure<string>(ProviderFailure()) : tag;
    }

    /// <inheritdoc />
    public async Task<Result> CompleteChunkedAsync(
        BlobPath path,
        string uploadId,
        IReadOnlyList<string> partTags,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));
        Guard.NotNullOrWhiteSpace(uploadId, nameof(uploadId));
        Guard.NotNull(partTags, nameof(partTags));

        var body = new StringBuilder("<CompleteMultipartUpload>");

        for (int i = 0; i < partTags.Count; i++)
        {
            body.Append("<Part><PartNumber>")
                .Append((i + 1).ToString(CultureInfo.InvariantCulture))
                .Append("</PartNumber><ETag>")
                .Append(partTags[i])
                .Append("</ETag></Part>");
        }

        body.Append("</CompleteMultipartUpload>");

        using HttpRequestMessage request = Sign(
            HttpMethod.Post,
            KeyFor(path),
            Encoding.UTF8.GetBytes(body.ToString()),
            CanonicalQuery("uploadId=" + Uri.EscapeDataString(uploadId)));

        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result> AbortChunkedAsync(
        BlobPath path,
        string uploadId,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));
        Guard.NotNullOrWhiteSpace(uploadId, nameof(uploadId));

        using HttpRequestMessage request = Sign(
            HttpMethod.Delete,
            KeyFor(path),
            Array.Empty<byte>(),
            CanonicalQuery("uploadId=" + Uri.EscapeDataString(uploadId)));

        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Result.Failure(NotFound());
        }

        return response.IsSuccessStatusCode ? Result.Success() : Result.Failure(ProviderFailure());
    }

    private static string KeyFor(BlobPath path) => path.Value;

    private HttpRequestMessage Sign(
        HttpMethod method,
        string key,
        byte[] payload,
        string canonicalQuery = "")
    {
        DateTimeOffset now = _clock.UtcNow;
        string payloadHash = _signer.HashPayload(payload);

        string basePath = _endpoint.UsePathStyle ? "/" + _endpoint.Bucket : string.Empty;
        string fullPath = key.Length == 0 ? basePath + "/" : basePath + "/" + key;
        string canonicalUri = AwsSignatureV4.EncodePath(fullPath);

        var uri = new Uri(
            _endpoint.ServiceUri,
            canonicalUri + (canonicalQuery.Length > 0 ? "?" + canonicalQuery : string.Empty));

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = uri.IdnHost + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture)),
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = AwsSignatureV4.AmzDate(now),
        };

        string canonicalRequest = AwsSignatureV4.CanonicalRequest(
            method.Method, canonicalUri, canonicalQuery, headers, payloadHash);

        string authorization = _signer.AuthorizationHeader(
            _credentials,
            _endpoint.Region,
            Service,
            now,
            canonicalRequest,
            AwsSignatureV4.SignedHeaders(headers));

        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("x-amz-date", headers["x-amz-date"]);

        if (payload.Length > 0)
        {
            request.Content = new ByteArrayContent(payload);
        }

        return request;
    }

    private static string CanonicalQuery(string query)
    {
        // S3 requires the query sorted by parameter name for signing. Built
        // here rather than at each call site so a caller cannot produce a
        // request that signs one order and sends another.
        string[] pairs = query.Split('&');
        Array.Sort(pairs, StringComparer.Ordinal);
        return string.Join("&", pairs);
    }

    private static string? ParseListing(string xml, List<string> into)
    {
        XDocument document = XDocument.Parse(xml);
        XNamespace ns = document.Root?.Name.Namespace ?? XNamespace.None;

        foreach (XElement contents in document.Descendants(ns + "Contents"))
        {
            string? key = contents.Element(ns + "Key")?.Value;

            // Sidecars are an implementation detail of this backend and are not
            // blobs. Returning them would make every stored file appear twice.
            if (key is not null && !key.EndsWith(MetadataSuffix, StringComparison.Ordinal))
            {
                into.Add(key);
            }
        }

        bool truncated = string.Equals(
            document.Descendants(ns + "IsTruncated").FirstOrDefault()?.Value, "true", StringComparison.Ordinal);

        return truncated
            ? document.Descendants(ns + "NextContinuationToken").FirstOrDefault()?.Value
            : null;
    }

    private static Error NotFound() => new(
        ErrorCodes.NotFound,
        "The requested resource was not found.",
        ErrorCategory.NotFound);

    private static Error ProviderFailure() => new(
        ErrorCodes.ProviderFailure,
        "The storage backend could not complete the operation.",
        ErrorCategory.Transient);
}
