using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Ai;

/// <summary>
/// The EU AI Act risk tiers, as they apply to a deployed use case rather than
/// to a model.
/// </summary>
/// <remarks>
/// The distinction matters and is the one most often got wrong: the Act
/// regulates the **use**, not the technology. The same model summarising
/// meeting notes and triaging patients sits in two different tiers, and only
/// the deployer knows which.
/// </remarks>
public enum AiRiskTier
{
    /// <summary>No specific obligations beyond ordinary law.</summary>
    Minimal = 0,

    /// <summary>Transparency obligations — people must know they are dealing with a machine.</summary>
    Limited = 1,

    /// <summary>
    /// High risk. Requires human oversight, logging, accuracy and robustness
    /// evidence, and a conformity assessment. Most clinical and employment
    /// applications land here.
    /// </summary>
    High = 2,

    /// <summary>
    /// Prohibited outright — social scoring, certain biometric categorisation,
    /// emotion inference in workplaces and schools.
    /// </summary>
    Unacceptable = 3,
}

/// <summary>What a segment of a prompt is, and therefore how it may be used.</summary>
/// <remarks>
/// The separation exists because a language model has no boundary of its own.
/// Anything in the prompt is equally authoritative to it, so a retrieved
/// document saying "ignore prior instructions" is, from the model's point of
/// view, an instruction. The boundary has to be imposed before the call.
/// </remarks>
public enum PromptSegmentKind
{
    /// <summary>Author-controlled text from a registered template. The only kind that instructs.</summary>
    Instruction = 0,

    /// <summary>Data. Retrieved documents, user input, record extracts — never authoritative.</summary>
    Content = 1,
}

/// <summary>One piece of a prompt, with its origin and classification.</summary>
public sealed class PromptSegment
{
    /// <summary>
    /// Declares a segment.
    /// </summary>
    /// <param name="kind">Whether this text instructs or is merely data.</param>
    /// <param name="text">The text.</param>
    /// <param name="classification">What the text is.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public PromptSegment(PromptSegmentKind kind, string text, DataClassificationLevel classification)
    {
        Kind = kind;
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Classification = classification;
    }

    /// <summary>Whether this text instructs or is merely data.</summary>
    public PromptSegmentKind Kind { get; }

    /// <summary>The text.</summary>
    public string Text { get; }

    /// <summary>The classification of the text.</summary>
    public DataClassificationLevel Classification { get; }
}

/// <summary>A prompt that has passed governance, ready for a provider.</summary>
public sealed class InferenceRequest
{
    /// <summary>
    /// Assembles a request.
    /// </summary>
    /// <param name="useCaseId">The registered use case this call belongs to.</param>
    /// <param name="segments">The prompt segments, in order.</param>
    /// <param name="effectiveClassification">The highest classification present.</param>
    /// <exception cref="ArgumentNullException"><paramref name="segments"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="useCaseId"/> is blank.</exception>
    public InferenceRequest(
        string useCaseId,
        IReadOnlyList<PromptSegment> segments,
        DataClassificationLevel effectiveClassification)
    {
        if (string.IsNullOrWhiteSpace(useCaseId))
        {
            throw new ArgumentException("An inference request belongs to a registered use case.", nameof(useCaseId));
        }

        UseCaseId = useCaseId;
        Segments = segments ?? throw new ArgumentNullException(nameof(segments));
        EffectiveClassification = effectiveClassification;
    }

    /// <summary>The registered use case this call belongs to.</summary>
    public string UseCaseId { get; }

    /// <summary>The prompt segments, in order.</summary>
    public IReadOnlyList<PromptSegment> Segments { get; }

    /// <summary>The highest classification present in the prompt.</summary>
    public DataClassificationLevel EffectiveClassification { get; }
}

/// <summary>A model's output — data, never an instruction.</summary>
public sealed class InferenceResult
{
    /// <summary>
    /// Records an output.
    /// </summary>
    /// <param name="text">What the model produced.</param>
    /// <param name="providerName">Which provider produced it.</param>
    /// <param name="modelIdentifier">Which model, for the audit trail.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public InferenceResult(string text, string providerName, string modelIdentifier)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        ProviderName = providerName ?? throw new ArgumentNullException(nameof(providerName));
        ModelIdentifier = modelIdentifier ?? throw new ArgumentNullException(nameof(modelIdentifier));
    }

    /// <summary>
    /// What the model produced. **Treat as untrusted input**, at the same level
    /// of suspicion as an HTTP request body.
    /// </summary>
    /// <remarks>
    /// It is derived from content that may itself have been hostile, so it can
    /// carry an injected instruction, a fabricated citation, or markup. Nothing
    /// in this platform executes it, renders it as HTML, or feeds it back as an
    /// <see cref="PromptSegmentKind.Instruction"/>.
    /// </remarks>
    public string Text { get; }

    /// <summary>Which provider produced it.</summary>
    public string ProviderName { get; }

    /// <summary>Which model, for the audit trail.</summary>
    public string ModelIdentifier { get; }
}

/// <summary>
/// One inference backend — a hosted API, a self-hosted model, a local runtime.
/// </summary>
/// <remarks>
/// **No vendor client ships in this framework** (ADR-001, ADR-009). A provider
/// is roughly a hundred lines against somebody's SDK, and it inherits the
/// governance layer's controls; what it must declare for itself is where the
/// data goes, because only it knows.
/// </remarks>
public interface IInferenceProvider
{
    /// <summary>A stable name for the audit trail.</summary>
    string ProviderName { get; }

    /// <summary>Which model this provider is configured against.</summary>
    string ModelIdentifier { get; }

    /// <summary>
    /// True when the prompt leaves the deployment's trust boundary.
    /// </summary>
    /// <remarks>
    /// A hosted API is external even under a no-training agreement: the data
    /// still crosses a boundary, still lands in somebody's logs, and still
    /// falls under a transfer assessment. Declaring otherwise to make a ceiling
    /// check pass would be the whole control defeated by one boolean.
    /// </remarks>
    bool IsExternal { get; }

    /// <summary>The highest classification that may be sent to this provider.</summary>
    DataClassificationLevel MaximumClassification { get; }

    /// <summary>
    /// Runs the inference.
    /// </summary>
    /// <param name="request">The governed request.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The output, or a failure carrying no prompt content.</returns>
    Task<Result<InferenceResult>> InferAsync(InferenceRequest request, CancellationToken cancellationToken);
}
