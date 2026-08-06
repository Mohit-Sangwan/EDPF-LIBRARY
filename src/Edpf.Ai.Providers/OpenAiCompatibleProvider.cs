using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Ai;
using Edpf.Abstractions.Primitives;
using Edpf.Ai;
using Edpf.Core.Guards;

namespace Edpf.Ai.Providers;

/// <summary>
/// An <see cref="IInferenceProvider"/> and <see cref="IEmbeddingProvider"/>
/// for any endpoint speaking the OpenAI chat-completions API.
/// </summary>
/// <remarks>
/// <para>
/// **One adapter, six of the named vendors.** OpenAI, Azure OpenAI, Mistral,
/// Ollama, NVIDIA NIM and Hugging Face TGI all expose
/// <c>/v1/chat/completions</c> with the same request and response shape, as do
/// vLLM, Groq, Together and LM Studio. They differ by base address, model name,
/// and whether the deployment considers the endpoint external.
/// </para>
/// <para>
/// No vendor SDK, for the reason the storage adapters established: an SDK
/// wrapper can only be exercised against the live service, and this can be
/// verified against a fake handler on every commit.
/// </para>
/// <para>
/// **The instruction/content separation survives the wire format**, and that is
/// the part worth reviewing. Instruction segments become the <c>system</c>
/// message; content segments become <c>user</c> messages. Flattening them into
/// one string would undo the whole governance layer at the final step — the
/// model would receive retrieved documents with exactly the authority of the
/// operator's instruction.
/// </para>
/// </remarks>
public sealed class OpenAiCompatibleProvider : IInferenceProvider, IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly Uri _baseAddress;
    private readonly string _embeddingModel;

    /// <summary>
    /// Composes the provider.
    /// </summary>
    /// <param name="http">The transport. Its lifetime belongs to the caller.</param>
    /// <param name="baseAddress">
    /// The API root — <c>https://api.openai.com</c>, a Mistral endpoint, or
    /// <c>http://localhost:11434</c> for Ollama.
    /// </param>
    /// <param name="apiKey">The bearer token, resolved from a secret store.</param>
    /// <param name="modelIdentifier">The chat model.</param>
    /// <param name="isExternal">
    /// Whether the endpoint is outside the deployment's trust boundary.
    /// Declared rather than inferred: a self-hosted Ollama on the cluster is
    /// internal, and a hosted API is external even under a no-training
    /// agreement.
    /// </param>
    /// <param name="maximumClassification">The highest classification that may be sent.</param>
    /// <param name="providerName">A stable name for the audit trail.</param>
    /// <param name="embeddingModel">The embedding model, when this provider is used for embeddings.</param>
    /// <param name="embeddingDimensions">The dimension of that model's vectors.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// A string argument is blank, or the endpoint is neither HTTPS nor a
    /// loopback address.
    /// </exception>
    public OpenAiCompatibleProvider(
        HttpClient http,
        Uri baseAddress,
        string apiKey,
        string modelIdentifier,
        bool isExternal,
        DataClassificationLevel maximumClassification,
        string providerName = "OpenAiCompatible",
        string embeddingModel = "text-embedding-3-small",
        int embeddingDimensions = 1536)
    {
        _http = Guard.NotNull(http, nameof(http));
        _baseAddress = Guard.NotNull(baseAddress, nameof(baseAddress));
        _apiKey = Guard.NotNullOrWhiteSpace(apiKey, nameof(apiKey));
        ModelIdentifier = Guard.NotNullOrWhiteSpace(modelIdentifier, nameof(modelIdentifier));
        ProviderName = Guard.NotNullOrWhiteSpace(providerName, nameof(providerName));
        _embeddingModel = Guard.NotNullOrWhiteSpace(embeddingModel, nameof(embeddingModel));

        // Plain HTTP is permitted only to loopback, which is how a self-hosted
        // model actually runs. Anywhere else it would put the API key and the
        // prompt — which may be PHI — on the wire in the clear.
        bool loopback = baseAddress.IsLoopback;
        if (!loopback && !string.Equals(baseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "An inference endpoint must be HTTPS unless it is loopback. Plain HTTP discloses both the "
                + "API key and the prompt.",
                nameof(baseAddress));
        }

        IsExternal = isExternal;
        MaximumClassification = maximumClassification;
        Dimensions = embeddingDimensions;
    }

    /// <inheritdoc />
    public string ProviderName { get; }

    /// <inheritdoc />
    public string ModelIdentifier { get; }

    /// <inheritdoc />
    public bool IsExternal { get; }

    /// <inheritdoc />
    public DataClassificationLevel MaximumClassification { get; }

    /// <inheritdoc />
    public int Dimensions { get; }

    /// <summary>Tokens reported by the last completed call, for cost tracking.</summary>
    public long LastTotalTokens { get; private set; }

    /// <inheritdoc />
    public async Task<Result<InferenceResult>> InferAsync(
        InferenceRequest request,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(request, nameof(request));

        string body = BuildChatRequest(request);

        Result<JsonDocument> response = await PostAsync(
            "/v1/chat/completions", body, cancellationToken).ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result.Failure<InferenceResult>(response.Error!);
        }

        using JsonDocument document = response.Value;

        if (!document.RootElement.TryGetProperty("choices", out JsonElement choices)
            || choices.GetArrayLength() == 0
            || !choices[0].TryGetProperty("message", out JsonElement message)
            || !message.TryGetProperty("content", out JsonElement content))
        {
            return Result.Failure<InferenceResult>(Malformed());
        }

        LastTotalTokens = ReadTotalTokens(document.RootElement);

        return new InferenceResult(content.GetString() ?? string.Empty, ProviderName, ModelIdentifier);
    }

    /// <inheritdoc />
    public async Task<Result<float[]>> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        Guard.NotNull(text, nameof(text));

        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["model"] = _embeddingModel,
            ["input"] = text,
        };

        Result<JsonDocument> response = await PostAsync(
            "/v1/embeddings", JsonSerializer.Serialize(payload), cancellationToken).ConfigureAwait(false);

        if (response.IsFailure)
        {
            return Result.Failure<float[]>(response.Error!);
        }

        using JsonDocument document = response.Value;

        if (!document.RootElement.TryGetProperty("data", out JsonElement data)
            || data.GetArrayLength() == 0
            || !data[0].TryGetProperty("embedding", out JsonElement embedding))
        {
            return Result.Failure<float[]>(Malformed());
        }

        var vector = new float[embedding.GetArrayLength()];
        int index = 0;

        foreach (JsonElement component in embedding.EnumerateArray())
        {
            vector[index++] = component.GetSingle();
        }

        LastTotalTokens = ReadTotalTokens(document.RootElement);
        return vector;
    }

    /// <summary>
    /// Maps prompt segments onto chat roles.
    /// </summary>
    /// <remarks>
    /// Instruction becomes <c>system</c>; content becomes <c>user</c>. The two
    /// never merge. A single flattened string would give a retrieved document
    /// the same standing as the operator's instruction, which is exactly the
    /// property the governance layer exists to prevent — and it would be lost
    /// here, at the last step, invisibly.
    /// </remarks>
    private string BuildChatRequest(InferenceRequest request)
    {
        var messages = new List<Dictionary<string, string>>();

        foreach (PromptSegment segment in request.Segments)
        {
            messages.Add(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["role"] = segment.Kind == PromptSegmentKind.Instruction ? "system" : "user",
                ["content"] = segment.Text,
            });
        }

        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["model"] = ModelIdentifier,
            ["messages"] = messages,
        };

        return JsonSerializer.Serialize(payload);
    }

    private async Task<Result<JsonDocument>> PostAsync(
        string path,
        string body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseAddress, path))
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
        };

        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=utf-8");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);

        using HttpResponseMessage response = await _http
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // The provider's own error body routinely echoes the prompt back.
            // It is discarded rather than surfaced, because a prompt may be
            // clinical and an error message travels further than a log line.
            return Result.Failure<JsonDocument>(new Error(
                ErrorCodes.IntegrationFailed,
                "The " + ProviderName + " endpoint rejected the request.",
                response.StatusCode is HttpStatusCode.TooManyRequests
                    or HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.GatewayTimeout
                        ? ErrorCategory.Transient
                        : ErrorCategory.Integration));
        }

        string content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            return Result.Failure<JsonDocument>(Malformed());
        }
    }

    private static long ReadTotalTokens(JsonElement root)
    {
        if (root.TryGetProperty("usage", out JsonElement usage)
            && usage.TryGetProperty("total_tokens", out JsonElement total)
            && total.TryGetInt64(out long tokens))
        {
            return tokens;
        }

        return 0;
    }

    private static Error Malformed() => new(
        ErrorCodes.SchemaMismatch,
        "The inference endpoint returned a response this client could not read.",
        ErrorCategory.Integration);
}
