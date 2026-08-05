using System;
using System.Collections.Generic;
using System.Globalization;
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
/// An <see cref="IBlobBackend"/> over Azure Files (SMB shares, REST endpoint).
/// </summary>
/// <remarks>
/// <para>
/// A separate adapter from <see cref="AzureBlobBackend"/> because it is a
/// separate protocol, not a different URL. Three differences drive everything
/// here:
/// </para>
/// <list type="number">
///   <item>
///     **A file is created at its final size, then written.** There is no
///     single-request upload: <c>PUT</c> with <c>x-ms-content-length</c>
///     allocates, and a second <c>PUT ?comp=range</c> fills it.
///   </item>
///   <item>
///     **Directories are real objects and must exist first.** Blob storage has
///     no directories — a slash is just a character in a key. Here, writing
///     <c>a/b/c.txt</c> into a share with no <c>a</c> fails.
///   </item>
///   <item>
///     **Listing is per directory and not by prefix.** Enumerating a subtree
///     means walking it.
///   </item>
/// </list>
/// <para>
/// The signing is shared with the Blob backend, including the <c>Range</c> slot
/// that the Blob service never uses and this one always does.
/// </para>
/// </remarks>
public sealed class AzureFilesBackend : IBlobBackend
{
    private readonly HttpClient _http;
    private readonly AzureCredentials _credentials;
    private readonly AzureSharedKeySignature _signer;
    private readonly IClock _clock;
    private readonly string _share;
    private readonly Uri _serviceUri;

    /// <summary>
    /// Composes the backend.
    /// </summary>
    /// <param name="http">The transport. Its lifetime belongs to the caller.</param>
    /// <param name="serviceUri">The file service root — <c>https://account.file.core.windows.net</c>.</param>
    /// <param name="share">The file share.</param>
    /// <param name="credentials">Account name and key, resolved from a secret store.</param>
    /// <param name="hashing">The hashing seam (Z.10).</param>
    /// <param name="clock">Time source.</param>
    /// <exception cref="ArgumentNullException">Any dependency is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="share"/> is blank.</exception>
    public AzureFilesBackend(
        HttpClient http,
        Uri serviceUri,
        string share,
        AzureCredentials credentials,
        IHashingService hashing,
        IClock clock)
    {
        _http = Guard.NotNull(http, nameof(http));
        _serviceUri = Guard.NotNull(serviceUri, nameof(serviceUri));
        _share = Guard.NotNullOrWhiteSpace(share, nameof(share));
        _credentials = Guard.NotNull(credentials, nameof(credentials));
        _clock = Guard.NotNull(clock, nameof(clock));
        _signer = new AzureSharedKeySignature(Guard.NotNull(hashing, nameof(hashing)));
    }

    /// <summary>The sidecar file suffix. Reserved.</summary>
    internal const string MetadataSuffix = ".edpfmeta";

    private const char Separator = '\u001F';

    /// <inheritdoc />
    public string BackendName => "AzureFiles";

    /// <inheritdoc />
    public async Task<Result> PutAsync(BlobPath path, byte[] bytes, CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));
        Guard.NotNull(bytes, nameof(bytes));

        return await WriteFileAsync(path.Value, bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<byte[]>> GetAsync(BlobPath path, CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));

        using HttpRequestMessage request = Sign(HttpMethod.Get, ResourcePath(path.Value), 0, null);
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

        using HttpRequestMessage request = Sign(HttpMethod.Delete, ResourcePath(path.Value), 0, null);
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
            HttpMethod.Delete, ResourcePath(path.Value + MetadataSuffix), 0, null);

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

        var found = new List<string>();
        string root = renderedPrefix.TrimEnd('/');

        Result walked = await WalkAsync(root, found, cancellationToken).ConfigureAwait(false);

        // A directory that does not exist is an empty listing, not an error.
        // "No documents yet" and "storage is down" must stay distinguishable.
        return walked.IsFailure && walked.Error!.Code != ErrorCodes.NotFound
            ? Result.Failure<IReadOnlyList<string>>(walked.Error!)
            : Result<IReadOnlyList<string>>.FromValue(found);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyDictionary<string, string>>> GetMetadataAsync(
        BlobPath path,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(path, nameof(path));

        using HttpRequestMessage request = Sign(
            HttpMethod.Get, ResourcePath(path.Value + MetadataSuffix), 0, null);

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

        var builder = new StringBuilder();
        foreach (KeyValuePair<string, string> entry in metadata)
        {
            builder.Append(entry.Key).Append(Separator).Append(entry.Value).Append('\n');
        }

        return await WriteFileAsync(
            path.Value + MetadataSuffix,
            Encoding.UTF8.GetBytes(builder.ToString()),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates every missing ancestor directory, then creates and fills the
    /// file.
    /// </summary>
    /// <remarks>
    /// Three round trips minimum, and that is inherent to the protocol rather
    /// than a shortcoming of this code. It is also the reason a deployment
    /// storing many small artefacts should prefer Blob over Files.
    /// </remarks>
    private async Task<Result> WriteFileAsync(string key, byte[] bytes, CancellationToken cancellationToken)
    {
        Result directories = await EnsureDirectoriesAsync(key, cancellationToken).ConfigureAwait(false);
        if (directories.IsFailure)
        {
            return directories;
        }

        // Step one: allocate at the final length.
        var createHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["x-ms-type"] = "file",
            ["x-ms-content-length"] = bytes.Length.ToString(CultureInfo.InvariantCulture),
        };

        using (HttpRequestMessage create = Sign(HttpMethod.Put, ResourcePath(key), 0, null, createHeaders))
        {
            using HttpResponseMessage created = await _http
                .SendAsync(create, cancellationToken)
                .ConfigureAwait(false);

            if (!created.IsSuccessStatusCode)
            {
                return Result.Failure(ProviderFailure());
            }
        }

        if (bytes.Length == 0)
        {
            // An empty file is fully described by its allocation. A ranged
            // write of zero bytes is rejected, not ignored.
            return Result.Success();
        }

        // Step two: fill it.
        string range = "bytes=0-" + (bytes.Length - 1).ToString(CultureInfo.InvariantCulture);

        var writeHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["x-ms-write"] = "update",
        };

        var query = new Dictionary<string, string>(StringComparer.Ordinal) { ["comp"] = "range" };

        using HttpRequestMessage write = Sign(
            HttpMethod.Put, ResourcePath(key), bytes.Length, query, writeHeaders, range);

        write.Content = new ByteArrayContent(bytes);
        write.Content.Headers.TryAddWithoutValidation("Content-Length", bytes.Length.ToString(CultureInfo.InvariantCulture));

        using HttpResponseMessage written = await _http
            .SendAsync(write, cancellationToken)
            .ConfigureAwait(false);

        return written.IsSuccessStatusCode ? Result.Success() : Result.Failure(ProviderFailure());
    }

    private async Task<Result> EnsureDirectoriesAsync(string key, CancellationToken cancellationToken)
    {
        string[] segments = key.Split('/');
        var query = new Dictionary<string, string>(StringComparer.Ordinal) { ["restype"] = "directory" };

        var current = new StringBuilder();

        // Every segment except the last, which is the file itself.
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (current.Length > 0)
            {
                current.Append('/');
            }

            current.Append(segments[i]);

            using HttpRequestMessage request = Sign(
                HttpMethod.Put, ResourcePath(current.ToString()), 0, query);

            using HttpResponseMessage response = await _http
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            // 409 Conflict means it already exists, which is the common case
            // and is success as far as this method is concerned.
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Conflict)
            {
                return Result.Failure(ProviderFailure());
            }
        }

        return Result.Success();
    }

    private async Task<Result> WalkAsync(string directory, List<string> into, CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["comp"] = "list",
            ["restype"] = "directory",
        };

        using HttpRequestMessage request = Sign(HttpMethod.Get, ResourcePath(directory), 0, query);
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

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        XDocument document = XDocument.Parse(body);

        var subdirectories = new List<string>();

        foreach (XElement entry in document.Descendants("Entries").Elements())
        {
            string? name = entry.Element("Name")?.Value;
            if (name is null)
            {
                continue;
            }

            string child = directory.Length == 0 ? name : directory + "/" + name;

            if (string.Equals(entry.Name.LocalName, "Directory", StringComparison.Ordinal))
            {
                subdirectories.Add(child);
            }
            else if (!name.EndsWith(MetadataSuffix, StringComparison.Ordinal))
            {
                into.Add(child);
            }
        }

        foreach (string subdirectory in subdirectories)
        {
            Result walked = await WalkAsync(subdirectory, into, cancellationToken).ConfigureAwait(false);
            if (walked.IsFailure && walked.Error!.Code != ErrorCodes.NotFound)
            {
                return walked;
            }
        }

        return Result.Success();
    }

    private string ResourcePath(string key) => "/" + _share + "/" + key;

    private HttpRequestMessage Sign(
        HttpMethod method,
        string path,
        long contentLength,
        Dictionary<string, string>? query,
        Dictionary<string, string>? extraHeaders = null,
        string range = "")
    {
        var msHeaders = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["x-ms-date"] = AzureSharedKeySignature.MsDate(_clock.UtcNow),
            ["x-ms-version"] = AzureSharedKeySignature.ApiVersion,
        };

        if (extraHeaders is not null)
        {
            foreach (KeyValuePair<string, string> header in extraHeaders)
            {
                msHeaders[header.Key] = header.Value;
            }
        }

        string canonicalResource = AzureSharedKeySignature.CanonicalResource(
            _credentials.AccountName, path, query);

        string stringToSign = AzureSharedKeySignature.StringToSign(
            method.Method, contentLength, msHeaders, canonicalResource, range);

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

        if (range.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        request.Headers.TryAddWithoutValidation(
            "Authorization", _signer.AuthorizationHeader(_credentials, stringToSign));

        return request;
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
