using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Ai;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Security;
using Edpf.Core.Guards;

namespace Edpf.Ai;

/// <summary>A registered, versioned prompt.</summary>
/// <remarks>
/// <para>
/// **Versioned because "why did the model say that in March" is a question
/// somebody will ask in June**, and it cannot be answered against a prompt that
/// has since been edited. The inference log records the version and the hash,
/// so the exact instruction that ran is recoverable even after the template has
/// moved on.
/// </para>
/// <para>
/// The same reasoning as consent versioning: consent to v3 is not consent to
/// v4's broader scope, and an answer produced by prompt v3 is not evidence
/// about prompt v4.
/// </para>
/// </remarks>
public sealed class VersionedPrompt
{
    /// <summary>
    /// Registers a prompt.
    /// </summary>
    /// <param name="promptId">The prompt's stable id.</param>
    /// <param name="version">Its version. Incremented on every text change.</param>
    /// <param name="instruction">The author-controlled instruction text.</param>
    /// <param name="hashing">The hashing seam, used to fingerprint the text.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The id or instruction is blank, or the version is below 1.</exception>
    public VersionedPrompt(string promptId, int version, string instruction, IHashingService hashing)
    {
        PromptId = Guard.NotNullOrWhiteSpace(promptId, nameof(promptId));
        Instruction = Guard.NotNullOrWhiteSpace(instruction, nameof(instruction));
        Guard.NotNull(hashing, nameof(hashing));

        if (version < 1)
        {
            throw new ArgumentException("Prompt versions start at 1.", nameof(version));
        }

        Version = version;

        byte[] digest = hashing.Sha256(Encoding.UTF8.GetBytes(instruction));

        // Lowercase hex to match every other digest in this codebase — the
        // audit chain, the blob content hash, the SigV4 signature. CA1308
        // prefers upper case for normalisation, but this is a rendering of a
        // hash for comparison against those, not a normalisation.
#pragma warning disable CA1308
        InstructionHash = Convert.ToHexString(digest).ToLowerInvariant();
#pragma warning restore CA1308
    }

    /// <summary>The prompt's stable id.</summary>
    public string PromptId { get; }

    /// <summary>Its version.</summary>
    public int Version { get; }

    /// <summary>The instruction text.</summary>
    public string Instruction { get; }

    /// <summary>
    /// Lowercase hex SHA-256 of the instruction.
    /// </summary>
    /// <remarks>
    /// Recorded alongside the version because a version number is a claim and a
    /// hash is a fact. An edit that forgot to bump the version is visible here
    /// and nowhere else.
    /// </remarks>
    public string InstructionHash { get; }
}

/// <summary>What a guardrail concluded.</summary>
public sealed class GuardrailVerdict
{
    private GuardrailVerdict(bool permitted, string? reason)
    {
        IsPermitted = permitted;
        Reason = reason;
    }

    /// <summary>True when the content may proceed.</summary>
    public bool IsPermitted { get; }

    /// <summary>Why it was blocked. Never quotes the content.</summary>
    public string? Reason { get; }

    /// <summary>Permits the content.</summary>
    public static GuardrailVerdict Allow() => new(true, null);

    /// <summary>
    /// Blocks the content.
    /// </summary>
    /// <param name="reason">
    /// Why, in terms a user can act on. **Must not quote the content** — a
    /// guardrail message that echoes what it blocked has published it.
    /// </param>
    public static GuardrailVerdict Block(string reason)
        => new(false, Guard.NotNullOrWhiteSpace(reason, nameof(reason)));
}

/// <summary>A check applied to a prompt before it is sent, or an output before it is returned.</summary>
public interface IGuardrail
{
    /// <summary>A stable name for the audit trail.</summary>
    string GuardrailName { get; }

    /// <summary>Checks content about to be sent to a model.</summary>
    /// <param name="text">The assembled prompt text.</param>
    /// <returns>The verdict.</returns>
    GuardrailVerdict CheckInput(string text);

    /// <summary>Checks content a model produced.</summary>
    /// <param name="text">The model output.</param>
    /// <returns>The verdict.</returns>
    GuardrailVerdict CheckOutput(string text);
}

/// <summary>
/// Blocks prompts and outputs that carry the shapes of an injected instruction.
/// </summary>
/// <remarks>
/// <para>
/// **This is defence in depth, and it is the weaker half of the defence.** The
/// strong half is structural: instruction and content arrive as separate
/// arguments and content is never promoted. This guardrail exists because
/// retrieved passages are attacker-influenced in a RAG system — a patient
/// letter, an uploaded PDF, a scanned referral — and a pattern check catches
/// the crude attempts that the structure alone renders inert but that still
/// indicate somebody is trying.
/// </para>
/// <para>
/// It is deliberately not sold as a solution. Prompt injection is not solved by
/// pattern matching, and a deployment that relies on this instead of the
/// classification ceiling has misunderstood which control is load-bearing.
/// </para>
/// </remarks>
public sealed class InjectionHeuristicGuardrail : IGuardrail
{
    private static readonly string[] Signals =
    [
        "ignore previous instruction",
        "ignore prior instruction",
        "ignore all previous",
        "disregard the above",
        "system prompt",
        "you are now",
        "reveal your instructions",
    ];

    /// <inheritdoc />
    public string GuardrailName => "InjectionHeuristic";

    /// <inheritdoc />
    public GuardrailVerdict CheckInput(string text)
    {
        Guard.NotNull(text, nameof(text));

        foreach (string signal in Signals)
        {
            if (text.Contains(signal, StringComparison.OrdinalIgnoreCase))
            {
                // Names the category, never the matched text. Echoing it would
                // put the injection attempt into the log that an operator reads.
                return GuardrailVerdict.Block(
                    "The content contains a phrase associated with prompt injection.");
            }
        }

        return GuardrailVerdict.Allow();
    }

    /// <inheritdoc />
    public GuardrailVerdict CheckOutput(string text)
    {
        Guard.NotNull(text, nameof(text));
        return GuardrailVerdict.Allow();
    }
}

/// <summary>Refuses model output that is empty or absurdly long.</summary>
/// <remarks>
/// The length bound is not about cost — the cost tracker handles that. It is
/// that a runaway generation is the observable symptom of a model that has lost
/// the plot, and passing megabytes of it to a caller who expected a paragraph
/// turns one bad response into a downstream incident.
/// </remarks>
public sealed class OutputShapeGuardrail : IGuardrail
{
    private readonly int _maxCharacters;

    /// <summary>Bounds the output.</summary>
    /// <param name="maxCharacters">The largest acceptable output.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxCharacters"/> is not positive.</exception>
    public OutputShapeGuardrail(int maxCharacters = 100_000)
    {
        if (maxCharacters < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        }

        _maxCharacters = maxCharacters;
    }

    /// <inheritdoc />
    public string GuardrailName => "OutputShape";

    /// <inheritdoc />
    public GuardrailVerdict CheckInput(string text) => GuardrailVerdict.Allow();

    /// <inheritdoc />
    public GuardrailVerdict CheckOutput(string text)
    {
        Guard.NotNull(text, nameof(text));

        if (text.Length == 0)
        {
            return GuardrailVerdict.Block("The model returned nothing.");
        }

        return text.Length > _maxCharacters
            ? GuardrailVerdict.Block("The model output exceeds the configured maximum length.")
            : GuardrailVerdict.Allow();
    }
}

/// <summary>Counts tokens and enforces a budget.</summary>
/// <remarks>
/// <para>
/// **Budgets fail closed.** A use case that has spent its allowance stops
/// running rather than continuing to spend, because the failure mode of the
/// alternative is a bill discovered at the end of the month and a model call
/// nobody authorised in the middle of it.
/// </para>
/// <para>
/// Token counts are approximated at four characters per token rather than
/// tokenised properly. That is stated rather than hidden: an exact count needs
/// the model's own tokenizer, which is vendor-specific and would put a vendor
/// dependency in the core. The approximation is adequate for a budget guard and
/// is not adequate for billing reconciliation, and the two should not be
/// confused.
/// </para>
/// </remarks>
public sealed class CostTracker
{
    private const int CharactersPerToken = 4;

    private readonly Dictionary<string, long> _tokensByUseCase = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _budgets = new(StringComparer.Ordinal);

    /// <summary>Sets a token budget for a use case.</summary>
    /// <param name="useCaseId">The use case.</param>
    /// <param name="tokenBudget">The allowance. Zero means unlimited.</param>
    public void SetBudget(string useCaseId, long tokenBudget)
        => _budgets[Guard.NotNullOrWhiteSpace(useCaseId, nameof(useCaseId))] = tokenBudget;

    /// <summary>Tokens spent so far by a use case.</summary>
    /// <param name="useCaseId">The use case.</param>
    /// <returns>The count.</returns>
    public long TokensSpent(string useCaseId)
        => _tokensByUseCase.TryGetValue(
            Guard.NotNullOrWhiteSpace(useCaseId, nameof(useCaseId)), out long spent) ? spent : 0;

    /// <summary>Approximates the token count of some text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The approximate token count.</returns>
    public static long EstimateTokens(string text)
    {
        Guard.NotNull(text, nameof(text));
        return (text.Length + CharactersPerToken - 1) / CharactersPerToken;
    }

    /// <summary>
    /// Checks whether a use case may spend the given tokens.
    /// </summary>
    /// <param name="useCaseId">The use case.</param>
    /// <param name="tokens">What the call would cost.</param>
    /// <returns>Success, or a refusal naming the budget and not the content.</returns>
    public Result Authorize(string useCaseId, long tokens)
    {
        Guard.NotNullOrWhiteSpace(useCaseId, nameof(useCaseId));

        if (!_budgets.TryGetValue(useCaseId, out long budget) || budget == 0)
        {
            return Result.Success();
        }

        long projected = TokensSpent(useCaseId) + tokens;

        return projected > budget
            ? Result.Failure(new Error(
                ErrorCodes.RateLimited,
                "Use case " + useCaseId + " has exhausted its token budget of "
                + budget.ToString(CultureInfo.InvariantCulture) + ".",
                ErrorCategory.Compliance))
            : Result.Success();
    }

    /// <summary>Records tokens actually spent.</summary>
    /// <param name="useCaseId">The use case.</param>
    /// <param name="tokens">How many.</param>
    public void Record(string useCaseId, long tokens)
    {
        Guard.NotNullOrWhiteSpace(useCaseId, nameof(useCaseId));
        _tokensByUseCase[useCaseId] = TokensSpent(useCaseId) + tokens;
    }
}

/// <summary>
/// Retrieval-augmented generation, assembled so that retrieval cannot defeat
/// the governance layer.
/// </summary>
/// <remarks>
/// <para>
/// **The whole design is one sentence: a retrieved passage is content, and it
/// carries its own classification.** Everything else follows.
/// </para>
/// <list type="bullet">
///   <item>
///     Retrieval is tenant-scoped by the index, so a query cannot reach another
///     tenant's records.
///   </item>
///   <item>
///     The assembled prompt's effective classification is the highest of the
///     passages retrieved — so a RAG call over clinical notes is a PHI call,
///     and the provider ceiling refuses to send it to an external model. **This
///     is the control that stops "we added RAG" from becoming "we uploaded the
///     record system to a vendor".**
///   </item>
///   <item>
///     Passages enter as content segments, never as instruction, so a document
///     saying "ignore prior instructions" is data that says that.
///   </item>
/// </list>
/// </remarks>
public sealed class RagPipeline
{
    private readonly IVectorIndex _index;
    private readonly IEmbeddingProvider _embeddings;

    /// <summary>
    /// Composes the pipeline.
    /// </summary>
    /// <param name="index">Where passages are searched.</param>
    /// <param name="embeddings">How the query is embedded.</param>
    /// <exception cref="ArgumentNullException">Either dependency is null.</exception>
    public RagPipeline(IVectorIndex index, IEmbeddingProvider embeddings)
    {
        _index = Guard.NotNull(index, nameof(index));
        _embeddings = Guard.NotNull(embeddings, nameof(embeddings));
    }

    /// <summary>
    /// Retrieves passages for a question and assembles them as content
    /// segments.
    /// </summary>
    /// <param name="question">The user's question.</param>
    /// <param name="topK">How many passages to retrieve.</param>
    /// <param name="minimumScore">Passages below this similarity are not used.</param>
    /// <param name="cancellationToken">Cancels the retrieval.</param>
    /// <returns>
    /// The retrieved context, or a failure. The question is embedded through
    /// the provider, so a question containing PHI is itself subject to the
    /// embedding provider's ceiling.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="question"/> is blank.</exception>
    public async Task<Result<RetrievedContext>> RetrieveAsync(
        string question,
        int topK,
        double minimumScore,
        CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(question, nameof(question));

        Result<float[]> queryVector = await _embeddings
            .EmbedAsync(question, cancellationToken)
            .ConfigureAwait(false);

        if (queryVector.IsFailure)
        {
            return Result.Failure<RetrievedContext>(queryVector.Error!);
        }

        Result<IReadOnlyList<VectorMatch>> matches =
            _index.Search(queryVector.Value, topK, minimumScore);

        if (matches.IsFailure)
        {
            return Result.Failure<RetrievedContext>(matches.Error!);
        }

        var segments = new List<PromptSegment>(matches.Value.Count);
        DataClassificationLevel effective = DataClassificationLevel.Public;

        foreach (VectorMatch match in matches.Value)
        {
            // Content, always. A retrieved document is data about the world,
            // not an instruction from the operator, however it is phrased.
            segments.Add(new PromptSegment(
                PromptSegmentKind.Content,
                "[" + match.Chunk.SourceReference + "] " + match.Chunk.Text,
                match.Chunk.Classification));

            if (match.Chunk.Classification > effective)
            {
                effective = match.Chunk.Classification;
            }
        }

        return new RetrievedContext(segments, matches.Value, effective);
    }
}

/// <summary>Passages retrieved for a question, ready to be sent as content.</summary>
public sealed class RetrievedContext
{
    /// <summary>
    /// Records a retrieval.
    /// </summary>
    /// <param name="segments">The passages as content segments.</param>
    /// <param name="matches">The matches, for citation and diagnostics.</param>
    /// <param name="classification">The highest classification retrieved.</param>
    public RetrievedContext(
        IReadOnlyList<PromptSegment> segments,
        IReadOnlyList<VectorMatch> matches,
        DataClassificationLevel classification)
    {
        Segments = segments;
        Matches = matches;
        Classification = classification;
    }

    /// <summary>The passages as content segments.</summary>
    public IReadOnlyList<PromptSegment> Segments { get; }

    /// <summary>The matches, for citation.</summary>
    public IReadOnlyList<VectorMatch> Matches { get; }

    /// <summary>
    /// The highest classification retrieved.
    /// </summary>
    /// <remarks>
    /// The number that decides whether this call may go to an external model.
    /// Retrieval over a clinical corpus produces PHI, and the provider ceiling
    /// then refuses — which is the control that stops "we added RAG" from
    /// becoming "we uploaded the record system to a vendor".
    /// </remarks>
    public DataClassificationLevel Classification { get; }
}
