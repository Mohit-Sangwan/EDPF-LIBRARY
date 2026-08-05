using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Communication;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Security;
using Edpf.Core.Guards;

namespace Edpf.Communication;

/// <summary>Which JSON shape a receiver expects.</summary>
/// <remarks>
/// The differences are trivial — one key name each — which is exactly why these
/// are one adapter rather than four. Three SDKs to choose between
/// <c>text</c> and <c>content</c> would be three supply chains for no
/// capability.
/// </remarks>
public enum WebhookFormat
{
    /// <summary>Slack incoming webhook: <c>{"text": "…"}</c>.</summary>
    Slack = 0,

    /// <summary>Microsoft Teams incoming webhook: <c>{"text": "…"}</c>.</summary>
    Teams = 1,

    /// <summary>Discord webhook: <c>{"content": "…"}</c>.</summary>
    Discord = 2,

    /// <summary>A generic receiver: <c>{"subject": "…", "body": "…"}</c>.</summary>
    Generic = 3,
}

/// <summary>
/// Posts messages to Slack, Teams, Discord or any JSON webhook receiver.
/// </summary>
/// <remarks>
/// <para>
/// **The webhook URL is a credential, not an address.** Anyone holding a Slack
/// or Teams incoming-webhook URL can post to that channel; there is no second
/// factor. So it comes from <c>ISecretStore</c>, it never appears in a log, and
/// it never appears in an error message — which is why every failure from this
/// class names the channel and not the endpoint.
/// </para>
/// <para>
/// **The ceiling defaults low and cannot sensibly be raised.** Slack, Teams and
/// Discord are third-party SaaS outside the deployment's trust boundary, and a
/// message posted there is retained, indexed and searchable by everyone in the
/// workspace. An alert saying "critical result for patient 4471, ward 3" is a
/// disclosure to the whole company, including the people who joined last week.
/// </para>
/// </remarks>
public sealed class WebhookChannel : ICommunicationChannel
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly WebhookFormat _format;
    private readonly IHashingService? _hashing;
    private readonly byte[]? _signingKey;

    /// <summary>
    /// Composes the channel.
    /// </summary>
    /// <param name="http">The transport. Its lifetime belongs to the caller.</param>
    /// <param name="channelName">The name templates address — <c>Slack</c>, <c>Ops</c>, and so on.</param>
    /// <param name="endpoint">The receiver's URL, resolved from a secret store.</param>
    /// <param name="format">The JSON shape the receiver expects.</param>
    /// <param name="maximumClassification">
    /// The ceiling. Defaults to <see cref="DataClassificationLevel.Internal"/>,
    /// which is already generous for a third-party workspace.
    /// </param>
    /// <param name="hashing">Hashing seam, required only when <paramref name="signingKey"/> is supplied.</param>
    /// <param name="signingKey">
    /// An optional HMAC key. When present, each request carries an
    /// <c>X-Edpf-Signature</c> header the receiver can verify — so a receiver
    /// with a leaked URL can still reject a forged post.
    /// </param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    /// <exception cref="ArgumentException">
    /// The channel name is blank, the endpoint is not HTTPS, or a signing key
    /// was supplied without a hashing service.
    /// </exception>
    public WebhookChannel(
        HttpClient http,
        string channelName,
        Uri endpoint,
        WebhookFormat format,
        DataClassificationLevel maximumClassification = DataClassificationLevel.Internal,
        IHashingService? hashing = null,
        byte[]? signingKey = null)
    {
        _http = Guard.NotNull(http, nameof(http));
        _endpoint = Guard.NotNull(endpoint, nameof(endpoint));
        ChannelName = Guard.NotNullOrWhiteSpace(channelName, nameof(channelName));
        _format = format;
        MaximumClassification = maximumClassification;

        // Refused at composition. The URL is the credential, and sending it
        // over plain HTTP hands it to anyone on the path — after which the
        // ceiling, the consent check and the template grammar are all
        // irrelevant.
        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A webhook endpoint must be HTTPS. The URL is itself the credential, and plain HTTP "
                + "discloses it to anyone on the path.",
                nameof(endpoint));
        }

        if (signingKey is not null && hashing is null)
        {
            throw new ArgumentException(
                "A signing key was supplied with no hashing service, so nothing would be signed.",
                nameof(hashing));
        }

        _hashing = hashing;
        _signingKey = signingKey;
    }

    /// <inheritdoc />
    public string ChannelName { get; }

    /// <summary>
    /// Webhooks address a room, not a person, so the recipient is an email-shaped
    /// identifier for the room itself.
    /// </summary>
    /// <remarks>
    /// This is the one place the framework's address model fits awkwardly, and
    /// the awkwardness is worth keeping: it means a template written for a chat
    /// room cannot be pointed at a patient's phone by changing one argument.
    /// </remarks>
    public AddressKind AddressKind => AddressKind.Email;

    /// <inheritdoc />
    public DataClassificationLevel MaximumClassification { get; }

    /// <inheritdoc />
    public async Task<Result> SendAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        Guard.NotNull(message, nameof(message));

        byte[] payload = Encoding.UTF8.GetBytes(Serialize(message));

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new ByteArrayContent(payload),
        };

        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=utf-8");

        if (_signingKey is not null)
        {
            string signature = ToHex(_hashing!.HmacSha256(_signingKey, payload));
            request.Headers.TryAddWithoutValidation("X-Edpf-Signature", "sha256=" + signature);
        }

        using HttpResponseMessage response = await _http
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        // A redirect is refused rather than followed. Following one would post
        // the payload — and the signature — to a host the deployment never
        // configured, which is an open relay with extra steps.
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            return Result.Failure(new Error(
                ErrorCodes.IntegrationFailed,
                "The " + ChannelName + " receiver answered with a redirect, which is not followed.",
                ErrorCategory.Integration));
        }

        if (!response.IsSuccessStatusCode)
        {
            // Names the channel, never the endpoint. The endpoint is the
            // credential and an error message travels further than a log line.
            return Result.Failure(new Error(
                ErrorCodes.IntegrationFailed,
                "The " + ChannelName + " receiver rejected the message.",
                response.StatusCode == HttpStatusCode.TooManyRequests
                    ? ErrorCategory.Transient
                    : ErrorCategory.Integration));
        }

        return Result.Success();
    }

    /// <summary>
    /// Builds the JSON body for the configured receiver.
    /// </summary>
    /// <remarks>
    /// Serialised with <see cref="JsonSerializer"/> rather than string
    /// concatenation. A message body is caller text that has already passed
    /// through a template, and a quote or a backslash in it would otherwise
    /// break out of the literal — the same injection shape as SQL, CSV and PDF,
    /// in the one format where hand-rolling the escaping looks easiest.
    /// </remarks>
    private string Serialize(OutboundMessage message)
    {
        string text = message.Subject.Length > 0
            ? message.Subject + ": " + message.Body
            : message.Body;

        Dictionary<string, string> body = _format switch
        {
            WebhookFormat.Discord => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["content"] = text,
            },

            WebhookFormat.Generic => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["subject"] = message.Subject,
                ["body"] = message.Body,
            },

            // Slack and Teams both take a "text" key. Kept as separate enum
            // members anyway, because the formats have diverged before and the
            // call site should say which receiver it meant.
            _ => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["text"] = text,
            },
        };

        return JsonSerializer.Serialize(body);
    }

    private static string ToHex(byte[] bytes)
    {
        const string Digits = "0123456789abcdef";
        var chars = new char[bytes.Length * 2];

        for (int i = 0; i < bytes.Length; i++)
        {
            chars[i * 2] = Digits[bytes[i] >> 4];
            chars[(i * 2) + 1] = Digits[bytes[i] & 0x0F];
        }

        return new string(chars);
    }
}
