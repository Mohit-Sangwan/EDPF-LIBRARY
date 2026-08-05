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

/// <summary>
/// An <see cref="IBlobBackend"/> over Azure Blob Storage.
/// </summary>
/// <remarks>
/// <para>
/// **Covers Azure Blob and Azure Data Lake Storage Gen2.** A Gen2 account is a
/// blob account with a hierarchical namespace, and it serves the Blob endpoint
/// for exactly these operations — put, get, delete, list by prefix. The
/// separate Data Lake API adds directory semantics and POSIX ACLs, which this
/// backend does not need: the store above it has its own path model and its own
/// authorization.
/// </para>
/// <para>
/// **Azure Files is deliberately not covered here, and it is not laziness.**
/// The Files REST API is a different protocol: a file must be created at its
/// final length first, then written with ranged PUTs, and directories must
/// exist before their contents. Pretending one adapter serves both would mean
/// an adapter that silently fails on the second one, which is precisely the
/// kind of claim this framework refuses to make.
/// </para>
/// </remarks>
public sealed class AzureBlobBackend : IBlobBackend
{
    private readonly HttpClient _http;
    private readonly AzureCredentials _credentials;
    private readonly AzureSharedKeySignature _signer;
    private readonly IClock _clock;
    private readonly string _container;
    private readonly Uri _serviceUri;

    /// <summary>
    /// Composes the backend.
    /// </summary>
    /// <param name="http">The transport. Its lifetime belongs to the caller.</param>
    /// <param name="serviceUri">
    /// The blob service root — <c>https://account.blob.core.windows.net</c>, or
    /// an Azurite endpoint for local development.
    /// </param>
    /// <param name="container">The container.</param>
    /// <param name="credentials">Account name and key, resolved from a secret store.</param>
    /// <param name="hashing">The hashing seam (Z.10).</param>
    /// <param name="clock">Time source. Azure rejects a request whose date skews by more than 15 minutes.</param>
    /// <exception cref="ArgumentNullException">Any dependency is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="container"/> is blank.</exception>
    public AzureBlobBackend(
        HttpClient http,
        Uri serviceUri,
        string container,
        AzureCredentials credentials,
        IHashingService hashing,
        IClock clock)
    {
        _http = Guard.NotNull(http, nameof(http));
        _serviceUri = Guard.NotNull(serviceUri, nameof(serviceUri));
        _container = Guard.NotNullOrWhiteSpace(container, nameof(container));
        _credentials = Guard.NotNull(credentials, nameof(credentials));
        _clock = Guard.NotNull(clock, nameof(clock));
        _signer = new AzureSharedKeySignature(Guard.NotNull(hashing, nameof(hashing)));
    }

    /// <summary>The sidecar key suffix. Reserved.</summary>
    internal const string MetadataSuffix = ".edpfmeta";

    private const char Separator = '\u001F';

    /// <inheritdoc />
    public string BackendName => "AzureBlob";

    /// <inheritdoc />
    public async Task<Result> PutAsync(BlobPath path, byte[] bytes, CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));
        Guard.NotNull(bytes, nameof(bytes));

        return await PutRawAsync(path.Value, bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<byte[]>> GetAsync(BlobPath path, CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));

        using HttpRequestMessage request = Sign(HttpMethod.Get, "/" + _container + "/" + path.Value, 0, null);
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

        using HttpRequestMessage request = Sign(
            HttpMethod.Delete, "/" + _container + "/" + path.Value, 0, null);

        using HttpResponseMessage response = await _http
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Result.Failure(NotFound());
        }

        if (!response.IsSuccessStatusCode)
        {
            return Result.Failure(ProviderFailure());
        }

        using HttpRequestMessage sidecar = Sign(
            HttpMethod.Delete, "/" + _container + "/" + path.Value + MetadataSuffix, 0, null);

        using HttpResponseMessage discarded = await _http
            .SendAsync(sidecar, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<string>>> ListAsync(
        string renderedPrefix,
        CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(renderedPrefix, nameof(renderedPrefix));

        var names = new List<string>();
        string? marker = null;

        do
        {
            var query = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["comp"] = "list",
                ["restype"] = "container",
                ["prefix"] = renderedPrefix,
            };

            if (marker is not null)
            {
                query["marker"] = marker;
            }

            using HttpRequestMessage request = Sign(
                HttpMethod.Get, "/" + _container, 0, query);

            using HttpResponseMessage response = await _http
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Result.Failure<IReadOnlyList<string>>(ProviderFailure());
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            marker = ParseListing(body, names);
        }
        while (!string.IsNullOrEmpty(marker));

        return names;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyDictionary<string, string>>> GetMetadataAsync(
        BlobPath path,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));

        using HttpRequestMessage request = Sign(
            HttpMethod.Get, "/" + _container + "/" + path.Value + MetadataSuffix, 0, null);

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

        // A sidecar blob rather than x-ms-meta-* headers, for the same reasons
        // as S3: Azure blob metadata must be ASCII header-safe, is size-capped,
        // and one of these values is a caller-supplied content type.
        var builder = new StringBuilder();
        foreach (KeyValuePair<string, string> entry in metadata)
        {
            builder.Append(entry.Key).Append(Separator).Append(entry.Value).Append('\n');
        }

        return await PutRawAsync(
            path.Value + MetadataSuffix,
            Encoding.UTF8.GetBytes(builder.ToString()),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result> PutRawAsync(string key, byte[] bytes, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = Sign(
            HttpMethod.Put,
            "/" + _container + "/" + key,
            bytes.Length,
            null,
            blockBlob: true);

        request.Content = new ByteArrayContent(bytes);

        using HttpResponseMessage response = await _http
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return response.IsSuccessStatusCode ? Result.Success() : Result.Failure(ProviderFailure());
    }

    private HttpRequestMessage Sign(
        HttpMethod method,
        string path,
        long contentLength,
        Dictionary<string, string>? query,
        bool blockBlob = false)
    {
        var msHeaders = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["x-ms-date"] = AzureSharedKeySignature.MsDate(_clock.UtcNow),
            ["x-ms-version"] = AzureSharedKeySignature.ApiVersion,
        };

        if (blockBlob)
        {
            msHeaders["x-ms-blob-type"] = "BlockBlob";
        }

        string canonicalResource = AzureSharedKeySignature.CanonicalResource(
            _credentials.AccountName, path, query);

        string stringToSign = AzureSharedKeySignature.StringToSign(
            method.Method, contentLength, msHeaders, canonicalResource);

        var builder = new StringBuilder(path);
        if (query is not null && query.Count > 0)
        {
            builder.Append('?');
            bool first = true;

            foreach (KeyValuePair<string, string> parameter in query)
            {
                if (!first)
                {
                    builder.Append('&');
                }

                builder.Append(parameter.Key).Append('=').Append(Uri.EscapeDataString(parameter.Value));
                first = false;
            }
        }

        var request = new HttpRequestMessage(method, new Uri(_serviceUri, builder.ToString()));

        foreach (KeyValuePair<string, string> header in msHeaders)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        request.Headers.TryAddWithoutValidation(
            "Authorization", _signer.AuthorizationHeader(_credentials, stringToSign));

        return request;
    }

    private static string? ParseListing(string xml, List<string> into)
    {
        XDocument document = XDocument.Parse(xml);

        foreach (XElement blob in document.Descendants("Blob"))
        {
            string? name = blob.Element("Name")?.Value;

            if (name is not null && !name.EndsWith(MetadataSuffix, StringComparison.Ordinal))
            {
                into.Add(name);
            }
        }

        return document.Descendants("NextMarker").FirstOrDefault()?.Value;
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
