using System.Net;
using System.Text;
using System.Text.Json;
using Edpf.Abstractions.Communication;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Security;
using Edpf.Communication;
using Edpf.UnitTests.TestDoubles;

namespace Edpf.UnitTests.Communication;

/// <summary>
/// The webhook channel — Slack, Teams, Discord and generic receivers, which
/// differ by one JSON key and are therefore one adapter.
/// </summary>
public sealed class WebhookChannelTests
{
    private readonly TestHashingService _hashing = new();

    private static readonly Uri Endpoint = new("https://hooks.example.com/services/T000/B000/XXXXsecretXXXX");

    private static (WebhookChannel Channel, RecordingHandler Handler) CreateChannel(
        WebhookFormat format = WebhookFormat.Slack,
        DataClassificationLevel ceiling = DataClassificationLevel.Internal,
        IHashingService? hashing = null,
        byte[]? signingKey = null)
    {
        var handler = new RecordingHandler();
        var http = new HttpClient(handler);

        return (new WebhookChannel(http, "Ops", Endpoint, format, ceiling, hashing, signingKey), handler);
    }

    private static OutboundMessage Message(string subject = "Alert", string body = "bed 4 free")
        => new(MessageAddress.ForEmail("ops@example.com"), subject, body, DataClassificationLevel.Internal);

    // ── the shapes ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(WebhookFormat.Slack, "text")]
    [InlineData(WebhookFormat.Teams, "text")]
    [InlineData(WebhookFormat.Discord, "content")]
    public async Task Send_UsesTheKeyTheReceiverExpects(WebhookFormat format, string expectedKey)
    {
        (WebhookChannel channel, RecordingHandler handler) = CreateChannel(format);

        Assert.True((await channel.SendAsync(Message(), default)).IsSuccess);

        using JsonDocument body = JsonDocument.Parse(Encoding.UTF8.GetString(handler.Bodies[0]));

        Assert.True(body.RootElement.TryGetProperty(expectedKey, out JsonElement value));
        Assert.Equal("Alert: bed 4 free", value.GetString());
    }

    [Fact]
    public async Task Send_ToAGenericReceiver_KeepsSubjectAndBodySeparate()
    {
        (WebhookChannel channel, RecordingHandler handler) = CreateChannel(WebhookFormat.Generic);

        await channel.SendAsync(Message(), default);

        using JsonDocument body = JsonDocument.Parse(Encoding.UTF8.GetString(handler.Bodies[0]));

        Assert.Equal("Alert", body.RootElement.GetProperty("subject").GetString());
        Assert.Equal("bed 4 free", body.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Send_EscapesABodyThatWouldBreakOutOfTheJsonLiteral()
    {
        // The same injection shape as SQL, CSV and PDF, in the one format where
        // hand-rolling the escaping looks easiest. Serialised properly, so a
        // quote is a quote.
        (WebhookChannel channel, RecordingHandler handler) = CreateChannel(WebhookFormat.Discord);

        string hostile = "\", \"content\": \"replaced\", \"x\": \"";
        await channel.SendAsync(Message(subject: string.Empty, body: hostile), default);

        using JsonDocument body = JsonDocument.Parse(Encoding.UTF8.GetString(handler.Bodies[0]));

        Assert.Equal(hostile, body.RootElement.GetProperty("content").GetString());
        Assert.False(body.RootElement.TryGetProperty("x", out _));
    }

    // ── the URL is a credential ───────────────────────────────────────────

    [Fact]
    public void Channel_RefusesAPlainHttpEndpoint()
    {
        // The URL is itself the credential; plain HTTP discloses it to anyone
        // on the path, after which every other control here is irrelevant.
        using var http = new HttpClient(new RecordingHandler());

        Assert.Throws<ArgumentException>(() => new WebhookChannel(
            http, "Ops", new Uri("http://hooks.example.com/x"), WebhookFormat.Slack));
    }

    [Fact]
    public async Task Send_FailureMessage_NeverContainsTheEndpoint()
    {
        // An error message travels further than a log line. Anyone holding this
        // URL can post to the channel.
        (WebhookChannel channel, RecordingHandler handler) = CreateChannel();
        handler.Status = HttpStatusCode.Forbidden;

        Result sent = await channel.SendAsync(Message(), default);

        Assert.True(sent.IsFailure);
        Assert.DoesNotContain("hooks.example.com", sent.Error!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("XXXXsecretXXXX", sent.Error.Message, StringComparison.Ordinal);
        Assert.Contains("Ops", sent.Error.Message, StringComparison.Ordinal);
    }

    // ── transport behaviour ───────────────────────────────────────────────

    [Fact]
    public async Task Send_RefusesToFollowARedirect()
    {
        // Following one would post the payload, and the signature, to a host
        // the deployment never configured. That is an open relay with extra
        // steps.
        (WebhookChannel channel, RecordingHandler handler) = CreateChannel();
        handler.Status = HttpStatusCode.TemporaryRedirect;

        Result sent = await channel.SendAsync(Message(), default);

        Assert.True(sent.IsFailure);
        Assert.Equal(ErrorCodes.IntegrationFailed, sent.Error!.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Send_TreatsRateLimitingAsTransientAndOtherRejectionsAsNot()
    {
        // The delivery scheduler retries on Transient and dead-letters on
        // anything else, so this mapping decides whether a throttled alert is
        // retried or abandoned.
        (WebhookChannel throttled, RecordingHandler throttledHandler) = CreateChannel();
        throttledHandler.Status = HttpStatusCode.TooManyRequests;

        (WebhookChannel rejected, RecordingHandler rejectedHandler) = CreateChannel();
        rejectedHandler.Status = HttpStatusCode.BadRequest;

        Result first = await throttled.SendAsync(Message(), default);
        Result second = await rejected.SendAsync(Message(), default);

        Assert.Equal(ErrorCategory.Transient, first.Error!.Category);
        Assert.Equal(ErrorCategory.Integration, second.Error!.Category);
    }

    [Fact]
    public async Task Send_PostsJson()
    {
        (WebhookChannel channel, RecordingHandler handler) = CreateChannel();

        await channel.SendAsync(Message(), default);

        HttpRequestMessage sent = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal(Endpoint, sent.RequestUri);
    }

    // ── signing ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Send_WithASigningKey_CarriesAVerifiableSignature()
    {
        // A receiver whose URL has leaked can still reject a forged post.
        byte[] key = Encoding.UTF8.GetBytes("shared-secret");
        (WebhookChannel channel, RecordingHandler handler) =
            CreateChannel(hashing: _hashing, signingKey: key);

        await channel.SendAsync(Message(), default);

        string header = handler.Requests[0].Headers.GetValues("X-Edpf-Signature").Single();
        byte[] expected = _hashing.HmacSha256(key, handler.Bodies[0]);

        Assert.StartsWith("sha256=", header, StringComparison.Ordinal);
        Assert.Equal(Convert.ToHexString(expected).ToLowerInvariant(), header.Substring("sha256=".Length));
    }

    [Fact]
    public async Task Send_WithoutASigningKey_SendsNoSignatureHeader()
    {
        (WebhookChannel channel, RecordingHandler handler) = CreateChannel();

        await channel.SendAsync(Message(), default);

        Assert.False(handler.Requests[0].Headers.Contains("X-Edpf-Signature"));
    }

    [Fact]
    public void Channel_RefusesASigningKeyWithNoHashingService()
    {
        // Otherwise the key is configured, nothing is signed, and the receiver
        // is verifying a header that never arrives.
        using var http = new HttpClient(new RecordingHandler());

        Assert.Throws<ArgumentException>(() => new WebhookChannel(
            http, "Ops", Endpoint, WebhookFormat.Slack,
            DataClassificationLevel.Internal, hashing: null, signingKey: [1, 2, 3]));
    }

    // ── the ceiling ───────────────────────────────────────────────────────

    [Fact]
    public void Channel_DefaultsToACeilingBelowPhi()
    {
        // A third-party workspace retains, indexes and exposes every message to
        // everyone in it, including the people who joined last week.
        (WebhookChannel channel, _) = CreateChannel();

        Assert.True(channel.MaximumClassification < DataClassificationLevel.Phi);
    }

    /// <summary>Captures requests and bodies. No network.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<byte[]> Bodies { get; } = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken));

            return new HttpResponseMessage(Status) { Content = new ByteArrayContent([]) };
        }
    }
}
