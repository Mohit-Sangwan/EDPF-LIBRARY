using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Edpf.Abstractions.Security;
using Edpf.Core.Guards;

namespace Edpf.Storage.Remote;

/// <summary>
/// Azure Storage Shared Key authorisation, implemented against
/// <see cref="IHashingService"/>.
/// </summary>
/// <remarks>
/// <para>
/// Same reasoning as <see cref="AwsSignatureV4"/>: a published, stable
/// algorithm is cheaper to implement than an SDK is to carry, and — the part
/// that actually matters — it can be verified without an Azure subscription.
/// </para>
/// <para>
/// The scheme is fiddlier than SigV4 in one specific way, and it is the way
/// people get it wrong: **the string to sign contains thirteen fixed header
/// slots in a fixed order**, most of which are usually empty, and an omitted
/// newline shifts everything after it. The resulting 403 says only
/// "signature did not match", so this is written out explicitly rather than
/// assembled in a loop.
/// </para>
/// </remarks>
public sealed class AzureSharedKeySignature
{
    private readonly IHashingService _hashing;

    /// <summary>
    /// Creates a signer.
    /// </summary>
    /// <param name="hashing">The hashing seam.</param>
    /// <exception cref="ArgumentNullException"><paramref name="hashing"/> is null.</exception>
    public AzureSharedKeySignature(IHashingService hashing)
        => _hashing = Guard.NotNull(hashing, nameof(hashing));

    /// <summary>The REST API version this signer targets.</summary>
    /// <remarks>
    /// Pinned rather than "latest". The string-to-sign layout is
    /// version-dependent — the empty-Content-Length rule below arrived in
    /// 2015-02-21 — so a floating version would change the signature format
    /// underneath a working deployment.
    /// </remarks>
    public const string ApiVersion = "2021-08-06";

    /// <summary>Formats an instant as an RFC 1123 date, which is what <c>x-ms-date</c> takes.</summary>
    /// <param name="instant">The instant.</param>
    /// <returns>e.g. <c>Wed, 05 Aug 2026 12:00:00 GMT</c>.</returns>
    public static string MsDate(DateTimeOffset instant)
        => instant.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Builds the string to sign.
    /// </summary>
    /// <param name="method">The HTTP method, upper case.</param>
    /// <param name="contentLength">The body length. Signed as empty when zero.</param>
    /// <param name="msHeaders">The <c>x-ms-*</c> headers, keyed by lowercase name.</param>
    /// <param name="canonicalResource">The canonicalised resource, from <see cref="CanonicalResource"/>.</param>
    /// <param name="range">
    /// The <c>Range</c> header, when one is sent. Empty otherwise. The Files
    /// service writes with ranged PUTs, so this slot is not decorative there
    /// even though the Blob service never fills it.
    /// </param>
    /// <returns>The string to sign.</returns>
    /// <exception cref="ArgumentNullException">Any reference argument is null.</exception>
    public static string StringToSign(
        string method,
        long contentLength,
        SortedDictionary<string, string> msHeaders,
        string canonicalResource,
        string range = "")
    {
        Guard.NotNull(method, nameof(method));
        Guard.NotNull(msHeaders, nameof(msHeaders));
        Guard.NotNull(canonicalResource, nameof(canonicalResource));

        var canonicalHeaders = new StringBuilder();
        foreach (KeyValuePair<string, string> header in msHeaders)
        {
            canonicalHeaders.Append(header.Key).Append(':').Append(header.Value.Trim()).Append('\n');
        }

        // Thirteen slots, in this order, whether or not they carry a value.
        // Content-Length is the exception that bites: it is the empty string
        // when zero, NOT "0", from API version 2015-02-21 onward.
        return method + "\n"
            + "\n"                                   // Content-Encoding
            + "\n"                                   // Content-Language
            + (contentLength == 0
                ? string.Empty
                : contentLength.ToString(CultureInfo.InvariantCulture)) + "\n"
            + "\n"                                   // Content-MD5
            + "\n"                                   // Content-Type
            + "\n"                                   // Date (superseded by x-ms-date)
            + "\n"                                   // If-Modified-Since
            + "\n"                                   // If-Match
            + "\n"                                   // If-None-Match
            + "\n"                                   // If-Unmodified-Since
            + range + "\n"                           // Range
            + canonicalHeaders
            + canonicalResource;
    }

    /// <summary>
    /// Builds the canonicalised resource: the account-prefixed path, then each
    /// query parameter on its own line, sorted.
    /// </summary>
    /// <param name="account">The storage account name.</param>
    /// <param name="path">The resource path, without the account.</param>
    /// <param name="query">Query parameters, or null.</param>
    /// <returns>The canonicalised resource.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="account"/> or <paramref name="path"/> is null.</exception>
    public static string CanonicalResource(
        string account,
        string path,
        IReadOnlyDictionary<string, string>? query)
    {
        Guard.NotNull(account, nameof(account));
        Guard.NotNull(path, nameof(path));

        var builder = new StringBuilder("/" + account + path);

        if (query is not null)
        {
            var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> parameter in query)
            {
                // CA1308 prefers ToUpperInvariant for normalisation, and here
                // that would be wrong: the Azure specification says the
                // canonicalised resource carries query parameter names in
                // LOWER case. This is a wire format, not a normalisation
                // choice, and upper case produces a valid-looking signature the
                // service rejects.
#pragma warning disable CA1308
                sorted[parameter.Key.ToLowerInvariant()] = parameter.Value;
#pragma warning restore CA1308
            }

            foreach (KeyValuePair<string, string> parameter in sorted)
            {
                builder.Append('\n').Append(parameter.Key).Append(':').Append(parameter.Value);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Produces the <c>Authorization</c> header value.
    /// </summary>
    /// <param name="credentials">Account name and key.</param>
    /// <param name="stringToSign">The string to sign.</param>
    /// <returns>e.g. <c>SharedKey account:base64signature</c>.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="FormatException">The account key is not valid base64.</exception>
    public string AuthorizationHeader(AzureCredentials credentials, string stringToSign)
    {
        Guard.NotNull(credentials, nameof(credentials));
        Guard.NotNull(stringToSign, nameof(stringToSign));

        // The account key is base64 of the raw HMAC key. Signing with the
        // base64 *text* is a classic error that produces a signature of
        // exactly the right shape and the wrong value.
        byte[] key = Convert.FromBase64String(credentials.AccountKey);
        byte[] signature = _hashing.HmacSha256(key, Encoding.UTF8.GetBytes(stringToSign));

        return "SharedKey " + credentials.AccountName + ":" + Convert.ToBase64String(signature);
    }
}

/// <summary>An Azure Storage account name and key.</summary>
/// <remarks>
/// The key must come from <c>ISecretStore</c> (Z.10). An account key grants
/// full control of the whole account, which is why a deployment should prefer
/// a delegated SAS or a managed identity where it can — and why this type is
/// the last hop rather than the store.
/// </remarks>
public sealed class AzureCredentials
{
    /// <summary>
    /// Holds credentials.
    /// </summary>
    /// <param name="accountName">The storage account name.</param>
    /// <param name="accountKey">The account key, base64, resolved from a secret store.</param>
    /// <exception cref="ArgumentException">Either value is blank.</exception>
    public AzureCredentials(string accountName, string accountKey)
    {
        AccountName = Guard.NotNullOrWhiteSpace(accountName, nameof(accountName));
        AccountKey = Guard.NotNullOrWhiteSpace(accountKey, nameof(accountKey));
    }

    /// <summary>The storage account name. Appears in the Authorization header.</summary>
    public string AccountName { get; }

    /// <summary>The account key. Never logged, never in an error message.</summary>
    public string AccountKey { get; }
}
