using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Ai;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Ai.Providers;

/// <summary>
/// An <see cref="IInferenceProvider"/> for the Anthropic Messages API.
/// </summary>
/// <remarks>
/// <para>
/// A separate adapter from <see cref="OpenAiCompatibleProvider"/> because the
/// wire format genuinely differs: the system prompt is a top-level
/// <c>system</c> field rather than a message with a role, <c>max_tokens</c> is
/// mandatory, authentication is <c>x-api-key</c> rather than a bearer token,
/// and the API version is a required header.
/// </para>
/// <para>
/// The governance layer above is unchanged, and that is the point of the
/// <see cref="IInferenceProvider"/> seam: use-case registration, the
/// classification ceiling, instruction/content separation and the audit log
/// apply identically whichever vendor is configured.
/// </para>
/// </remarks>
public sealed class AnthropicProvider : IInferenceProvider
{
    /// <summary>The API version this adapter targets.</summary>
    /// <remarks>
    /// Pinned rather than floating, for the same reason as the Azure storage
    /// API version: the request and response shapes are version-dependent, and
    /// "latest" would change them underneath a working deployment.
    /// </remarks>
    public const string ApiVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly Uri _baseAddress;
    private readonly int _maxTokens;

    /// <summary>
    /// Composes the provider.
    /// </summary>
    /// <param name="http">The transport. Its lifetime belongs to the caller.</param>
    /// <param name="baseAddress">The API root, normally <c>https://api.anthropic.com</c>.</param>
    /// <param name="apiKey">The API key, resolved from a secret store.</param>
    /// <param name="modelIdentifier">The model.</param>
    /// <param name="maximumClassification">The highest classification that may be sent.</param>
    /// <param name="maxTokens">
    /// The output bound. Mandatory in this API, so it is mandatory here rather
    /// than defaulted invisibly.
    /// </param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">A string argument is blank or the endpoint is not HTTPS.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxTokens"/> is not positive.</exception>
    public AnthropicProvider(
        HttpClient http,
        Uri baseAddress,
        string apiKey,
        string modelIdentifier,
        DataClassificationLevel maximumClassification,
        int maxTokens = 4096)
    {
        _http = Guard.NotNull(http, nameof(http));
        _baseAddress = Guard.NotNull(baseAddress, nameof(baseAddress));
        _apiKey = Guard.NotNullOrWhiteSpace(apiKey, nameof(apiKey));
        ModelIdentifier = Guard.NotNullOrWhiteSpace(modelIdentifier, nameof(modelIdentifier));

        if (!string.Equals(baseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "An inference endpoint must be HTTPS. Plain HTTP discloses both the API key and the prompt.",
                nameof(baseAddress));
        }

        if (maxTokens < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTokens));
        }

        MaximumClassification = maximumClassification;
        _maxTokens = maxTokens;
    }

    /// <inheritdoc />
    public string ProviderName => "Anthropic";

    /// <inheritdoc />
    public string ModelIdentifier { get; }

    /// <summary>
    /// True. A hosted API is outside the trust boundary even under a
    /// no-training agreement — the data still crosses it.
    /// </summary>
    /// <remarks>
    /// Fixed rather than configurable, unlike the OpenAI-compatible adapter
    /// where a self-hosted Ollama is a real internal deployment. There is no
    /// self-hosted Anthropic endpoint, so a constructor flag could only ever be
    /// used to tell the ceiling check something untrue.
    /// </remarks>
    public bool IsExternal => true;

    /// <inheritdoc />
    public DataClassificationLevel MaximumClassification { get; }

    /// <summary>Tokens reported by the last completed call, for cost tracking.</summary>
    public long LastTotalTokens { get; private set; }

    /// <inheritdoc />
    public async Task<Result<InferenceResult>> InferAsync(
        InferenceRequest request,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(request, nameof(request));

        // Instruction segments become the top-level system field; content
        // becomes user messages. The separation survives the mapping, which is
        // the one thing an adapter must not lose.
        var system = new StringBuilder();
        var messages = new List<Dictionary<string, string>>();

        foreach (PromptSegment segment in request.Segments)
        {
            if (segment.Kind == PromptSegmentKind.Instruction)
            {
                if (system.Length > 0)
                {
                    system.Append("\n\n");
                }

                system.Append(segment.Text);
                continue;
            }

            messages.Add(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["role"] = "user",
                ["content"] = segment.Text,
            });
        }

        // The API rejects an empty message list. A prompt with instruction and
        // no content is legitimate — "summarise the standing guidance" — so it
        // is carried as a single empty-content user turn rather than refused.
        if (messages.Count == 0)
        {
            messages.Add(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["role"] = "user",
                ["content"] = string.Empty,
            });
        }

        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["model"] = ModelIdentifier,
            ["max_tokens"] = _maxTokens,
            ["messages"] = messages,
        };

        if (system.Length > 0)
        {
            payload["system"] = system.ToString();
        }

        using var http = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseAddress, "/v1/messages"))
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))),
        };

        http.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=utf-8");
        http.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        http.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);

        using HttpResponseMessage response = await _http
            .SendAsync(http, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // The error body can echo the prompt. Discarded, because a prompt
            // may be clinical and an error travels further than a log line.
            return Result.Failure<InferenceResult>(new Error(
                ErrorCodes.IntegrationFailed,
                "The Anthropic endpoint rejected the request.",
                response.StatusCode is HttpStatusCode.TooManyRequests
                    or HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.GatewayTimeout
                        ? ErrorCategory.Transient
                        : ErrorCategory.Integration));
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("content", out JsonElement content)
                || content.GetArrayLength() == 0)
            {
                return Result.Failure<InferenceResult>(Malformed());
            }

            var text = new StringBuilder();
            foreach (JsonElement block in content.EnumerateArray())
            {
                // Blocks may be text or tool-use. Only text is read here; a
                // tool-use block is not silently rendered as prose, because a
                // caller that did not ask for tools should not receive their
                // arguments as an answer.
                if (block.TryGetProperty("type", out JsonElement type)
                    && string.Equals(type.GetString(), "text", StringComparison.Ordinal)
                    && block.TryGetProperty("text", out JsonElement value))
                {
                    text.Append(value.GetString());
                }
            }

            LastTotalTokens = ReadTokens(document.RootElement);

            return new InferenceResult(text.ToString(), ProviderName, ModelIdentifier);
        }
        catch (JsonException)
        {
            return Result.Failure<InferenceResult>(Malformed());
        }
    }

    private static long ReadTokens(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out JsonElement usage))
        {
            return 0;
        }

        long input = usage.TryGetProperty("input_tokens", out JsonElement inputTokens)
            && inputTokens.TryGetInt64(out long parsedInput) ? parsedInput : 0;

        long output = usage.TryGetProperty("output_tokens", out JsonElement outputTokens)
            && outputTokens.TryGetInt64(out long parsedOutput) ? parsedOutput : 0;

        // Summed, because this API reports the two separately while the cost
        // tracker counts one number. Reporting only input would understate
        // every call by the size of its answer.
        return input + output;
    }

    private static Error Malformed() => new(
        ErrorCodes.SchemaMismatch,
        "The inference endpoint returned a response this client could not read.",
        ErrorCategory.Integration);
}
