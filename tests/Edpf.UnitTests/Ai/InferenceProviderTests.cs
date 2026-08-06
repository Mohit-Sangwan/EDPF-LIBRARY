using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Edpf.Abstractions.Ai;
using Edpf.Abstractions.Primitives;
using Edpf.Ai.Providers;

namespace Edpf.UnitTests.Ai;

/// <summary>
/// The vendor adapters, verified without API keys. The property every test
/// here defends is that the instruction/content separation survives the
/// mapping onto each vendor's wire format — losing it at the final step would
/// undo the whole governance layer invisibly.
/// </summary>
public sealed class InferenceProviderTests
{
    private static InferenceRequest Request(params PromptSegment[] segments)
        => new("summarise", segments, DataClassificationLevel.Internal);

    private static PromptSegment Instruction(string text)
        => new(PromptSegmentKind.Instruction, text, DataClassificationLevel.Internal);

    private static PromptSegment Content(string text)
        => new(PromptSegmentKind.Content, text, DataClassificationLevel.Internal);

    // ── OpenAI-compatible ─────────────────────────────────────────────────

    private static (OpenAiCompatibleProvider Provider, RecordingHandler Handler) CreateOpenAi(
        Uri? baseAddress = null)
    {
        var handler = new RecordingHandler();
        var http = new HttpClient(handler);

        return (new OpenAiCompatibleProvider(
            http,
            baseAddress ?? new Uri("https://api.openai.com"),
            "sk-secret-key-value",
            "gpt-4o",
            isExternal: true,
            DataClassificationLevel.Internal), handler);
    }

    [Fact]
    public async Task OpenAi_MapsInstructionToSystemAndContentToUser()
    {
        // The one thing an adapter must not lose. A single flattened string
        // would give a retrieved document the same standing as the operator's
        // instruction.
        (OpenAiCompatibleProvider provider, RecordingHandler handler) = CreateOpenAi();
        handler.Body = ChatResponse("a summary");

        await provider.InferAsync(
            Request(Instruction("Summarise the notes."), Content("Patient improved.")), default);

        using JsonDocument sent = JsonDocument.Parse(Encoding.UTF8.GetString(handler.Bodies[0]));
        JsonElement messages = sent.RootElement.GetProperty("messages");

        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("Summarise the notes.", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("Patient improved.", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task OpenAi_ReadsTheCompletionAndTheTokenUsage()
    {
        (OpenAiCompatibleProvider provider, RecordingHandler handler) = CreateOpenAi();
        handler.Body = ChatResponse("a summary", totalTokens: 175);

        Result<InferenceResult> inferred = await provider.InferAsync(
            Request(Instruction("Summarise.")), default);

        Assert.True(inferred.IsSuccess);
        Assert.Equal("a summary", inferred.Value.Text);
        Assert.Equal("gpt-4o", inferred.Value.ModelIdentifier);
        Assert.Equal(175, provider.LastTotalTokens);
    }

    [Fact]
    public async Task OpenAi_EmbedsTextAndReturnsTheVector()
    {
        (OpenAiCompatibleProvider provider, RecordingHandler handler) = CreateOpenAi();
        handler.Body = """{"data":[{"embedding":[0.5,-0.25,1.0]}],"usage":{"total_tokens":7}}""";

        Result<float[]> embedded = await provider.EmbedAsync("some text", default);

        Assert.True(embedded.IsSuccess);
        Assert.Equal([0.5f, -0.25f, 1.0f], embedded.Value);
        Assert.EndsWith("/v1/embeddings", handler.Requests[0].RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAi_SendsABearerToken()
    {
        (OpenAiCompatibleProvider provider, RecordingHandler handler) = CreateOpenAi();
        handler.Body = ChatResponse("x");

        await provider.InferAsync(Request(Instruction("Summarise.")), default);

        Assert.Equal(
            "Bearer sk-secret-key-value",
            handler.Requests[0].Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public void OpenAi_RefusesAPlainHttpEndpointThatIsNotLoopback()
    {
        // Plain HTTP puts both the API key and the prompt on the wire, and the
        // prompt may be clinical.
        using var http = new HttpClient(new RecordingHandler());

        Assert.Throws<ArgumentException>(() => new OpenAiCompatibleProvider(
            http, new Uri("http://api.example.com"), "k", "m", true, DataClassificationLevel.Public));
    }

    [Fact]
    public void OpenAi_PermitsPlainHttpToLoopback()
    {
        // Which is how a self-hosted model actually runs.
        using var http = new HttpClient(new RecordingHandler());

        var provider = new OpenAiCompatibleProvider(
            http, new Uri("http://localhost:11434"), "k", "llama3",
            isExternal: false, DataClassificationLevel.Phi, providerName: "Ollama");

        Assert.False(provider.IsExternal);
        Assert.Equal(DataClassificationLevel.Phi, provider.MaximumClassification);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, ErrorCategory.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ErrorCategory.Transient)]
    [InlineData(HttpStatusCode.BadRequest, ErrorCategory.Integration)]
    [InlineData(HttpStatusCode.Unauthorized, ErrorCategory.Integration)]
    public async Task OpenAi_MapsStatusOntoRetryability(HttpStatusCode status, ErrorCategory expected)
    {
        // This mapping decides whether the delivery-style retry path tries
        // again or gives up. A throttle is temporary; a bad key is not.
        (OpenAiCompatibleProvider provider, RecordingHandler handler) = CreateOpenAi();
        handler.Status = status;
        handler.Body = """{"error":{"message":"..."}}""";

        Result<InferenceResult> inferred = await provider.InferAsync(
            Request(Instruction("x")), default);

        Assert.True(inferred.IsFailure);
        Assert.Equal(expected, inferred.Error!.Category);
    }

    [Fact]
    public async Task OpenAi_FailureNeverEchoesTheProvidersErrorBody()
    {
        // Vendor error bodies routinely quote the prompt back, and a prompt may
        // be clinical. An error message travels further than a log line.
        (OpenAiCompatibleProvider provider, RecordingHandler handler) = CreateOpenAi();
        handler.Status = HttpStatusCode.BadRequest;
        handler.Body = """{"error":{"message":"invalid input: MRN 000123 Alex Smith"}}""";

        Result<InferenceResult> inferred = await provider.InferAsync(
            Request(Content("MRN 000123 Alex Smith")), default);

        Assert.DoesNotContain("000123", inferred.Error!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Alex Smith", inferred.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAi_MalformedResponse_IsASchemaMismatchNotACrash()
    {
        (OpenAiCompatibleProvider provider, RecordingHandler handler) = CreateOpenAi();
        handler.Body = """{"unexpected":true}""";

        Result<InferenceResult> inferred = await provider.InferAsync(
            Request(Instruction("x")), default);

        Assert.Equal(ErrorCodes.SchemaMismatch, inferred.Error!.Code);
    }

    // ── Anthropic ─────────────────────────────────────────────────────────

    private static (AnthropicProvider Provider, RecordingHandler Handler) CreateAnthropic()
    {
        var handler = new RecordingHandler();
        var http = new HttpClient(handler);

        return (new AnthropicProvider(
            http,
            new Uri("https://api.anthropic.com"),
            "sk-ant-secret",
            "claude-sonnet-4",
            DataClassificationLevel.Internal), handler);
    }

    [Fact]
    public async Task Anthropic_PutsTheInstructionInTheTopLevelSystemField()
    {
        // A different wire shape for the same separation: system is a field
        // here, not a message with a role.
        (AnthropicProvider provider, RecordingHandler handler) = CreateAnthropic();
        handler.Body = MessagesResponse("a summary");

        await provider.InferAsync(
            Request(Instruction("Summarise the notes."), Content("Patient improved.")), default);

        using JsonDocument sent = JsonDocument.Parse(Encoding.UTF8.GetString(handler.Bodies[0]));

        Assert.Equal("Summarise the notes.", sent.RootElement.GetProperty("system").GetString());

        JsonElement messages = sent.RootElement.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("Patient improved.", messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Anthropic_SendsAnEmptyUserTurnWhenThereIsNoContent()
    {
        // The API rejects an empty message list, and a prompt with instruction
        // and no content is legitimate — "summarise the standing guidance".
        (AnthropicProvider provider, RecordingHandler handler) = CreateAnthropic();
        handler.Body = MessagesResponse("x");

        await provider.InferAsync(Request(Instruction("Summarise the guidance.")), default);

        using JsonDocument sent = JsonDocument.Parse(Encoding.UTF8.GetString(handler.Bodies[0]));

        Assert.Equal(1, sent.RootElement.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task Anthropic_SendsTheApiKeyAndVersionHeaders()
    {
        (AnthropicProvider provider, RecordingHandler handler) = CreateAnthropic();
        handler.Body = MessagesResponse("x");

        await provider.InferAsync(Request(Instruction("x")), default);

        Assert.Equal("sk-ant-secret", handler.Requests[0].Headers.GetValues("x-api-key").Single());
        Assert.Equal(
            AnthropicProvider.ApiVersion,
            handler.Requests[0].Headers.GetValues("anthropic-version").Single());
    }

    [Fact]
    public async Task Anthropic_SumsInputAndOutputTokens()
    {
        // The API reports the two separately and the cost tracker counts one
        // number. Reporting only input understates every call by its answer.
        (AnthropicProvider provider, RecordingHandler handler) = CreateAnthropic();
        handler.Body = MessagesResponse("x", inputTokens: 120, outputTokens: 35);

        await provider.InferAsync(Request(Instruction("x")), default);

        Assert.Equal(155, provider.LastTotalTokens);
    }

    [Fact]
    public async Task Anthropic_ReadsOnlyTextBlocks()
    {
        // A tool-use block is not silently rendered as prose. A caller that did
        // not ask for tools should not receive their arguments as an answer.
        (AnthropicProvider provider, RecordingHandler handler) = CreateAnthropic();
        handler.Body = """
            {"content":[
              {"type":"text","text":"the answer"},
              {"type":"tool_use","id":"t1","name":"lookup","input":{"mrn":"000123"}}
            ],"usage":{"input_tokens":1,"output_tokens":1}}
            """;

        Result<InferenceResult> inferred = await provider.InferAsync(Request(Instruction("x")), default);

        Assert.Equal("the answer", inferred.Value.Text);
        Assert.DoesNotContain("000123", inferred.Value.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Anthropic_IsAlwaysExternal()
    {
        // There is no self-hosted Anthropic endpoint, so a configurable flag
        // could only ever tell the ceiling check something untrue.
        (AnthropicProvider provider, _) = CreateAnthropic();

        Assert.True(provider.IsExternal);
    }

    [Fact]
    public void Anthropic_RefusesAPlainHttpEndpoint()
    {
        using var http = new HttpClient(new RecordingHandler());

        Assert.Throws<ArgumentException>(() => new AnthropicProvider(
            http, new Uri("http://api.anthropic.com"), "k", "m", DataClassificationLevel.Public));
    }

    // ── helpers ───────────────────────────────────────────────────────────

    // Built by concatenation rather than a raw interpolated literal: these
    // shapes end in runs of closing braces, and the `$$` count needed to make
    // that legal is more confusing than the concatenation it replaces.
    private static string ChatResponse(string text, int totalTokens = 10)
        => "{\"choices\":[{\"message\":{\"content\":\"" + text + "\"}}],"
            + "\"usage\":{\"total_tokens\":" + totalTokens.ToString(CultureInfo.InvariantCulture) + "}}";

    private static string MessagesResponse(string text, int inputTokens = 1, int outputTokens = 1)
        => "{\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}],"
            + "\"usage\":{\"input_tokens\":" + inputTokens.ToString(CultureInfo.InvariantCulture)
            + ",\"output_tokens\":" + outputTokens.ToString(CultureInfo.InvariantCulture) + "}}";

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<byte[]> Bodies { get; } = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public string Body { get; set; } = "{}";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken));

            return new HttpResponseMessage(Status)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Body)),
            };
        }
    }
}
