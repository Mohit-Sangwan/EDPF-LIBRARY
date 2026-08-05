using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Edpf.Abstractions.Security;
using Edpf.Core.Guards;

namespace Edpf.Storage.Remote;

/// <summary>
/// AWS Signature Version 4, implemented directly against
/// <see cref="IHashingService"/>.
/// </summary>
/// <remarks>
/// <para>
/// **Written rather than taken from an SDK, and that is a deliberate trade.**
/// The AWS SDK is a large dependency with its own transitive graph, its own
/// CVE cadence, and its own opinions about retries, credentials resolution and
/// logging — none of which this framework wants to inherit into every
/// deployment that stores a file. SigV4 itself is a published, stable, fully
/// specified algorithm that fits in one readable file.
/// </para>
/// <para>
/// The consequence that matters more: a signer built on <c>HttpClient</c>
/// and a hashing seam can be **tested without credentials**. A fake message
/// handler asserts that the correct canonical request and signature are
/// produced for a known key, using AWS's own published test vectors. An SDK
/// wrapper can only be tested against AWS, which is why untested cloud adapters
/// are the norm and why this repository has been caught by declared-but-never-
/// executed code six times.
/// </para>
/// <para>
/// Cryptography goes through <see cref="IHashingService"/> rather than
/// <c>System.Security.Cryptography</c> directly (Z.10), so this file needs no
/// exception to the rule.
/// </para>
/// </remarks>
public sealed class AwsSignatureV4
{
    private const string Algorithm = "AWS4-HMAC-SHA256";
    private const string Terminator = "aws4_request";

    private readonly IHashingService _hashing;

    /// <summary>
    /// Creates a signer.
    /// </summary>
    /// <param name="hashing">The hashing seam.</param>
    /// <exception cref="ArgumentNullException"><paramref name="hashing"/> is null.</exception>
    public AwsSignatureV4(IHashingService hashing) => _hashing = Guard.NotNull(hashing, nameof(hashing));

    /// <summary>
    /// The payload hash header value for an empty body.
    /// </summary>
    public const string EmptyPayloadHash =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>Formats an instant as an ISO-8601 basic timestamp.</summary>
    /// <param name="instant">The instant.</param>
    /// <returns>e.g. <c>20260805T120000Z</c>.</returns>
    public static string AmzDate(DateTimeOffset instant)
        => instant.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Formats an instant as a credential-scope date.</summary>
    /// <param name="instant">The instant.</param>
    /// <returns>e.g. <c>20260805</c>.</returns>
    public static string ScopeDate(DateTimeOffset instant)
        => instant.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>Lowercase hex of a digest.</summary>
    /// <param name="bytes">The digest.</param>
    /// <returns>The hex string.</returns>
    public static string ToHex(byte[] bytes)
    {
        Guard.NotNull(bytes, nameof(bytes));

        const string Digits = "0123456789abcdef";
        var chars = new char[bytes.Length * 2];

        for (int i = 0; i < bytes.Length; i++)
        {
            chars[i * 2] = Digits[bytes[i] >> 4];
            chars[(i * 2) + 1] = Digits[bytes[i] & 0x0F];
        }

        return new string(chars);
    }

    /// <summary>SHA-256 of a payload, hex encoded — the <c>x-amz-content-sha256</c> value.</summary>
    /// <param name="payload">The request body.</param>
    /// <returns>The hex digest.</returns>
    public string HashPayload(byte[] payload)
    {
        Guard.NotNull(payload, nameof(payload));
        return ToHex(_hashing.Sha256(payload));
    }

    /// <summary>
    /// Builds the canonical request string.
    /// </summary>
    /// <param name="method">The HTTP method, upper case.</param>
    /// <param name="canonicalPath">The path, already percent-encoded per segment.</param>
    /// <param name="canonicalQuery">The canonical query string, possibly empty.</param>
    /// <param name="headers">Headers to sign, keyed by lowercase name.</param>
    /// <param name="payloadHash">Hex SHA-256 of the body.</param>
    /// <returns>The canonical request.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static string CanonicalRequest(
        string method,
        string canonicalPath,
        string canonicalQuery,
        SortedDictionary<string, string> headers,
        string payloadHash)
    {
        Guard.NotNull(method, nameof(method));
        Guard.NotNull(canonicalPath, nameof(canonicalPath));
        Guard.NotNull(canonicalQuery, nameof(canonicalQuery));
        Guard.NotNull(headers, nameof(headers));
        Guard.NotNull(payloadHash, nameof(payloadHash));

        var canonicalHeaders = new StringBuilder();
        var signedHeaders = new StringBuilder();

        foreach (KeyValuePair<string, string> header in headers)
        {
            canonicalHeaders.Append(header.Key).Append(':').Append(header.Value.Trim()).Append('\n');

            if (signedHeaders.Length > 0)
            {
                signedHeaders.Append(';');
            }

            signedHeaders.Append(header.Key);
        }

        return method + "\n"
            + canonicalPath + "\n"
            + canonicalQuery + "\n"
            + canonicalHeaders + "\n"
            + signedHeaders + "\n"
            + payloadHash;
    }

    /// <summary>The semicolon-joined signed header names.</summary>
    /// <param name="headers">Headers to sign, keyed by lowercase name.</param>
    /// <returns>e.g. <c>host;x-amz-content-sha256;x-amz-date</c>.</returns>
    public static string SignedHeaders(SortedDictionary<string, string> headers)
    {
        Guard.NotNull(headers, nameof(headers));

        var builder = new StringBuilder();
        foreach (string name in headers.Keys)
        {
            if (builder.Length > 0)
            {
                builder.Append(';');
            }

            builder.Append(name);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Produces the <c>Authorization</c> header value.
    /// </summary>
    /// <param name="credentials">Access key and secret.</param>
    /// <param name="region">The region, e.g. <c>eu-west-1</c>.</param>
    /// <param name="service">The service, <c>s3</c>.</param>
    /// <param name="instant">The request instant.</param>
    /// <param name="canonicalRequest">The canonical request.</param>
    /// <param name="signedHeaders">The signed header list.</param>
    /// <returns>The header value.</returns>
    /// <exception cref="ArgumentNullException">Any reference argument is null.</exception>
    public string AuthorizationHeader(
        S3Credentials credentials,
        string region,
        string service,
        DateTimeOffset instant,
        string canonicalRequest,
        string signedHeaders)
    {
        Guard.NotNull(credentials, nameof(credentials));
        Guard.NotNull(region, nameof(region));
        Guard.NotNull(service, nameof(service));
        Guard.NotNull(canonicalRequest, nameof(canonicalRequest));
        Guard.NotNull(signedHeaders, nameof(signedHeaders));

        string scope = ScopeDate(instant) + "/" + region + "/" + service + "/" + Terminator;

        string stringToSign = Algorithm + "\n"
            + AmzDate(instant) + "\n"
            + scope + "\n"
            + ToHex(_hashing.Sha256(Encoding.UTF8.GetBytes(canonicalRequest)));

        byte[] signingKey = SigningKey(credentials.SecretAccessKey, ScopeDate(instant), region, service);
        string signature = ToHex(_hashing.HmacSha256(signingKey, Encoding.UTF8.GetBytes(stringToSign)));

        return Algorithm
            + " Credential=" + credentials.AccessKeyId + "/" + scope
            + ", SignedHeaders=" + signedHeaders
            + ", Signature=" + signature;
    }

    /// <summary>
    /// Derives the date-, region- and service-scoped signing key.
    /// </summary>
    /// <remarks>
    /// The chain is the reason a leaked signature is bounded: it authorises one
    /// service in one region on one day, and nothing else. That property is
    /// lost the moment somebody caches the wrong link in the chain.
    /// </remarks>
    private byte[] SigningKey(string secret, string date, string region, string service)
    {
        byte[] kDate = _hashing.HmacSha256(
            Encoding.UTF8.GetBytes("AWS4" + secret), Encoding.UTF8.GetBytes(date));
        byte[] kRegion = _hashing.HmacSha256(kDate, Encoding.UTF8.GetBytes(region));
        byte[] kService = _hashing.HmacSha256(kRegion, Encoding.UTF8.GetBytes(service));

        return _hashing.HmacSha256(kService, Encoding.UTF8.GetBytes(Terminator));
    }

    /// <summary>
    /// Percent-encodes a URI path, preserving separators.
    /// </summary>
    /// <param name="path">The path, with <c>/</c> separators.</param>
    /// <returns>The canonical path.</returns>
    /// <remarks>
    /// S3's canonicalisation is stricter than <see cref="Uri"/>'s: it requires
    /// uppercase hex, and it does not treat <c>!</c>, <c>*</c>, <c>'</c>,
    /// <c>(</c> or <c>)</c> as unreserved. A signature computed over a
    /// differently-encoded path fails with a message that names nothing useful,
    /// which is why this is spelled out rather than delegated.
    /// </remarks>
    public static string EncodePath(string path)
    {
        Guard.NotNull(path, nameof(path));

        var builder = new StringBuilder(path.Length + 16);

        foreach (char c in path)
        {
            bool unreserved = (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c is '-' or '.' or '_' or '~' or '/';

            if (unreserved)
            {
                builder.Append(c);
                continue;
            }

            foreach (byte b in Encoding.UTF8.GetBytes(c.ToString()))
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }
}

/// <summary>Static credentials for an S3-compatible endpoint.</summary>
/// <remarks>
/// Held as <see cref="string"/> because that is what a signing algorithm needs.
/// The secret must come from <c>ISecretStore</c> and never from configuration
/// or source (Z.10); this type is the last hop, not the store.
/// </remarks>
public sealed class S3Credentials
{
    /// <summary>
    /// Holds credentials.
    /// </summary>
    /// <param name="accessKeyId">The access key id.</param>
    /// <param name="secretAccessKey">The secret, resolved from a secret store.</param>
    /// <exception cref="ArgumentException">Either value is blank.</exception>
    public S3Credentials(string accessKeyId, string secretAccessKey)
    {
        AccessKeyId = Guard.NotNullOrWhiteSpace(accessKeyId, nameof(accessKeyId));
        SecretAccessKey = Guard.NotNullOrWhiteSpace(secretAccessKey, nameof(secretAccessKey));
    }

    /// <summary>The access key id. Appears in the Authorization header.</summary>
    public string AccessKeyId { get; }

    /// <summary>The secret. Never logged, never in an error message.</summary>
    public string SecretAccessKey { get; }
}
