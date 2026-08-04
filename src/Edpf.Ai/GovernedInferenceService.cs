using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Ai;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Tenancy;
using Edpf.Core.Guards;
using Edpf.Core.Time;

namespace Edpf.Ai;

/// <summary>
/// Holds an in-memory inference log. Suitable for a single node and for tests;
/// a deployment under the Act's logging obligation persists these.
/// </summary>
public sealed class InMemoryInferenceLog : IInferenceLog
{
    private readonly List<InferenceRecord> _records = [];

    /// <inheritdoc />
    public IReadOnlyList<InferenceRecord> Records => _records;

    /// <inheritdoc />
    public void Append(InferenceRecord record) => _records.Add(Guard.NotNull(record, nameof(record)));
}

/// <summary>
/// The governance layer between an application and a model (ADR-037; deferred
/// there, built on the sponsor's instruction).
/// </summary>
/// <remarks>
/// <para>
/// **This is not an LLM client and does not contain one.** It is the boundary
/// a regulated deployment needs around whichever client it chooses: a
/// registered use case with a declared risk tier, a provider that has stated
/// where the data goes, a ceiling that stops classified content crossing that
/// boundary, and a record of every call.
/// </para>
/// <para>
/// The prompt structure is the second half. A model has no boundary between
/// instruction and data — everything in the context window is equally
/// authoritative to it — so the boundary is imposed here: instruction segments
/// come only from the caller's registered template, content segments are
/// marked as data, and a caller cannot promote content to instruction because
/// <see cref="Infer"/> takes them as separate arguments.
/// </para>
/// </remarks>
public sealed class GovernedInferenceService
{
    private readonly Dictionary<string, AiUseCase> _useCases;
    private readonly IInferenceProvider _provider;
    private readonly IInferenceLog _log;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IClock _clock;

    /// <summary>
    /// Composes the governance layer over one provider.
    /// </summary>
    /// <param name="useCases">The registered use cases.</param>
    /// <param name="provider">The inference backend.</param>
    /// <param name="log">Where records go.</param>
    /// <param name="tenantAccessor">Ambient tenant.</param>
    /// <param name="clock">Time source (Z.3 rule 4).</param>
    /// <exception cref="ArgumentNullException">Any dependency is null.</exception>
    public GovernedInferenceService(
        IReadOnlyList<AiUseCase> useCases,
        IInferenceProvider provider,
        IInferenceLog log,
        ITenantContextAccessor tenantAccessor,
        IClock clock)
    {
        Guard.NotNull(useCases, nameof(useCases));
        _provider = Guard.NotNull(provider, nameof(provider));
        _log = Guard.NotNull(log, nameof(log));
        _tenantAccessor = Guard.NotNull(tenantAccessor, nameof(tenantAccessor));
        _clock = Guard.NotNull(clock, nameof(clock));

        _useCases = new Dictionary<string, AiUseCase>(StringComparer.Ordinal);
        foreach (AiUseCase useCase in useCases)
        {
            _useCases[useCase.UseCaseId] = useCase;
        }
    }

    /// <summary>
    /// Runs an inference under a registered use case.
    /// </summary>
    /// <param name="useCaseId">The registered use case.</param>
    /// <param name="instruction">
    /// Author-controlled text. This is the only argument the model is permitted
    /// to treat as an instruction.
    /// </param>
    /// <param name="content">
    /// Data — retrieved documents, record extracts, user input. Each segment
    /// carries its own classification, and the highest one governs.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The output, or a failure. Failures carry
    /// <see cref="ErrorCodes.NotFound"/> (no such use case),
    /// <see cref="ErrorCodes.ChannelClassificationExceeded"/> (the prompt is
    /// too sensitive for this provider), or
    /// <see cref="ErrorCodes.TenantScopeViolation"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public async Task<Result<InferenceResult>> Infer(
        string useCaseId,
        string instruction,
        IReadOnlyList<PromptSegment> content,
        CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(useCaseId, nameof(useCaseId));
        Guard.NotNullOrWhiteSpace(instruction, nameof(instruction));
        Guard.NotNull(content, nameof(content));

        ITenantContext? tenant = _tenantAccessor.Current;
        if (tenant is null || tenant.TenantId == Guid.Empty)
        {
            return Result.Failure<InferenceResult>(new Error(
                ErrorCodes.TenantScopeViolation,
                "The requested resource was not found.",
                ErrorCategory.NotFound));
        }

        if (!_useCases.TryGetValue(useCaseId, out AiUseCase? useCase))
        {
            // An unregistered use case is refused rather than run with
            // defaults. "Which model does what" is the question the whole
            // platform exists to be able to answer, and a default answer is
            // no answer.
            return Result.Failure<InferenceResult>(new Error(
                ErrorCodes.NotFound,
                "The requested resource was not found.",
                ErrorCategory.NotFound));
        }

        var segments = new List<PromptSegment>(content.Count + 1)
        {
            new(PromptSegmentKind.Instruction, instruction, DataClassificationLevel.Internal),
        };

        DataClassificationLevel effective = DataClassificationLevel.Internal;
        foreach (PromptSegment segment in content)
        {
            // A caller cannot smuggle an instruction in through the content
            // list. Everything here is data, whatever it was labelled as.
            segments.Add(new PromptSegment(PromptSegmentKind.Content, segment.Text, segment.Classification));

            if (segment.Classification > effective)
            {
                effective = segment.Classification;
            }
        }

        if (effective > _provider.MaximumClassification)
        {
            return Result.Failure<InferenceResult>(new Error(
                ErrorCodes.ChannelClassificationExceeded,
                "The " + _provider.ProviderName + " provider accepts at most "
                + _provider.MaximumClassification + " content"
                + (_provider.IsExternal ? " and is outside the trust boundary." : "."),
                ErrorCategory.Compliance));
        }

        var request = new InferenceRequest(useCaseId, segments, effective);

        Result<InferenceResult> result = await _provider
            .InferAsync(request, cancellationToken)
            .ConfigureAwait(false);

        // Recorded whether or not the provider succeeded. A failed call still
        // sent the data, and the question the log answers is "what left here",
        // not "what worked".
        _log.Append(new InferenceRecord(
            useCase.UseCaseId,
            useCase.RiskTier,
            _provider.ProviderName,
            _provider.ModelIdentifier,
            _provider.IsExternal,
            effective,
            StorableInstant.Normalize(_clock.UtcNow)));

        return result;
    }
}
