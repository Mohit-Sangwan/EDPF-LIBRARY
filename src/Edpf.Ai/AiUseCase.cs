using System;
using System.Collections.Generic;
using Edpf.Abstractions.Ai;

namespace Edpf.Ai;

/// <summary>
/// A registered application of a model, with the declarations the EU AI Act
/// requires of the deployer.
/// </summary>
/// <remarks>
/// <para>
/// **This type is the deliverable.** ADR-037 said as much: for a framework
/// selling into regulated healthcare, the valuable part of an AI platform is
/// not the embeddings — it is being able to answer, on the day a regulator or
/// a buyer's security team asks, what each model is used for, what risk tier
/// that use sits in, who oversees it, and where the data went.
/// </para>
/// <para>
/// Every field is a constructor parameter and none has a default, for the same
/// reason as <see cref="Abstractions.Storage.BlobWriteOptions"/>: each is a
/// declaration the platform cannot make on the deployer's behalf, and a
/// default would be the platform quietly asserting something about somebody
/// else's obligations.
/// </para>
/// </remarks>
public sealed class AiUseCase
{
    /// <summary>
    /// Registers a use case, refusing the ones that must not exist.
    /// </summary>
    /// <param name="useCaseId">A stable id, used in the audit trail.</param>
    /// <param name="purpose">What this use of the model is for, in a sentence.</param>
    /// <param name="riskTier">The tier this <em>use</em> sits in — not the model's.</param>
    /// <param name="informsClinicalDecision">
    /// Whether the output reaches a clinical decision. See the exception
    /// remarks: declaring this true is refused.
    /// </param>
    /// <param name="humanOversight">
    /// How a person supervises the output. Mandatory for
    /// <see cref="AiRiskTier.High"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <para>
    /// The id or purpose is blank; or the tier is
    /// <see cref="AiRiskTier.Unacceptable"/>; or the tier is
    /// <see cref="AiRiskTier.High"/> with no oversight declared; or
    /// <paramref name="informsClinicalDecision"/> is true.
    /// </para>
    /// <para>
    /// The clinical-decision refusal is ADR-023 restated where it can be
    /// enforced. An application whose model output informs diagnosis or
    /// treatment is a regulated medical device — FDA SaMD, EU MDR — and the
    /// obligations that attach are the builder's, not this framework's.
    /// Refusing the declaration does not stop anyone building it; it stops them
    /// building it *here* and inheriting an implied claim that EDPF's
    /// conformity work covers them. It does not.
    /// </para>
    /// </exception>
    public AiUseCase(
        string useCaseId,
        string purpose,
        AiRiskTier riskTier,
        bool informsClinicalDecision,
        string? humanOversight = null)
    {
        if (string.IsNullOrWhiteSpace(useCaseId))
        {
            throw new ArgumentException("A use case requires an id.", nameof(useCaseId));
        }

        if (string.IsNullOrWhiteSpace(purpose))
        {
            throw new ArgumentException(
                "A use case requires a stated purpose. 'General assistance' is not a purpose, and a "
                + "conformity assessment cannot be written against it.",
                nameof(purpose));
        }

        if (riskTier == AiRiskTier.Unacceptable)
        {
            throw new ArgumentException(
                "This risk tier is prohibited under the EU AI Act. There is no configuration that makes it "
                + "available.",
                nameof(riskTier));
        }

        if (informsClinicalDecision)
        {
            throw new ArgumentException(
                "Model output that informs a clinical decision makes the application a regulated medical "
                + "device (FDA SaMD / EU MDR). EDPF provides data infrastructure and does not make clinical "
                + "decisions (ADR-023); building it on this platform would imply a conformity claim that "
                + "does not exist.",
                nameof(informsClinicalDecision));
        }

        if (riskTier == AiRiskTier.High && string.IsNullOrWhiteSpace(humanOversight))
        {
            throw new ArgumentException(
                "A high-risk use case must declare how a person oversees it. Oversight that is not written "
                + "down is oversight nobody is rostered for.",
                nameof(humanOversight));
        }

        UseCaseId = useCaseId;
        Purpose = purpose;
        RiskTier = riskTier;
        HumanOversight = humanOversight;
    }

    /// <summary>A stable id, used in the audit trail.</summary>
    public string UseCaseId { get; }

    /// <summary>What this use of the model is for.</summary>
    public string Purpose { get; }

    /// <summary>The tier this use sits in.</summary>
    public AiRiskTier RiskTier { get; }

    /// <summary>How a person supervises the output; null below high risk.</summary>
    public string? HumanOversight { get; }

    /// <summary>
    /// True when the Act's transparency obligation applies — the person on the
    /// other end must be told they are dealing with a machine.
    /// </summary>
    public bool RequiresDisclosureToSubject => RiskTier >= AiRiskTier.Limited;
}

/// <summary>
/// What happened, for the record. Metadata only — no prompt, no output.
/// </summary>
/// <remarks>
/// The Act's logging obligation is about traceability of the system, not
/// retention of its inputs. Keeping prompts would turn the compliance log into
/// the largest uncontrolled store of clinical text in the deployment, which is
/// the opposite of the intent.
/// </remarks>
public sealed class InferenceRecord
{
    /// <summary>
    /// Records one governed inference.
    /// </summary>
    /// <param name="useCaseId">Which use case.</param>
    /// <param name="riskTier">Its declared tier.</param>
    /// <param name="providerName">Which provider ran it.</param>
    /// <param name="modelIdentifier">Which model.</param>
    /// <param name="wasExternal">Whether the prompt left the trust boundary.</param>
    /// <param name="classification">The highest classification in the prompt.</param>
    /// <param name="occurredUtc">When.</param>
    public InferenceRecord(
        string useCaseId,
        AiRiskTier riskTier,
        string providerName,
        string modelIdentifier,
        bool wasExternal,
        Abstractions.Primitives.DataClassificationLevel classification,
        DateTimeOffset occurredUtc)
    {
        UseCaseId = useCaseId;
        RiskTier = riskTier;
        ProviderName = providerName;
        ModelIdentifier = modelIdentifier;
        WasExternal = wasExternal;
        Classification = classification;
        OccurredUtc = occurredUtc;
    }

    /// <summary>Which use case.</summary>
    public string UseCaseId { get; }

    /// <summary>Its declared tier.</summary>
    public AiRiskTier RiskTier { get; }

    /// <summary>Which provider ran it.</summary>
    public string ProviderName { get; }

    /// <summary>Which model.</summary>
    public string ModelIdentifier { get; }

    /// <summary>Whether the prompt left the trust boundary.</summary>
    public bool WasExternal { get; }

    /// <summary>The highest classification in the prompt.</summary>
    public Abstractions.Primitives.DataClassificationLevel Classification { get; }

    /// <summary>When it happened.</summary>
    public DateTimeOffset OccurredUtc { get; }
}

/// <summary>Where inference records go.</summary>
public interface IInferenceLog
{
    /// <summary>Appends a record.</summary>
    /// <param name="record">What happened.</param>
    void Append(InferenceRecord record);

    /// <summary>Everything recorded, oldest first.</summary>
    IReadOnlyList<InferenceRecord> Records { get; }
}
