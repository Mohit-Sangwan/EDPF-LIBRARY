using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Communication;
using Edpf.Abstractions.Compliance;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Tenancy;
using Edpf.Core.Guards;

namespace Edpf.Communication;

/// <summary>
/// The policy layer for outbound communication (ADR-037 v1.0 addition 3).
/// </summary>
/// <remarks>
/// <para>
/// Four controls, applied in an order that is itself part of the design:
/// </para>
/// <list type="number">
///   <item>A resolved tenant, or nothing proceeds.</item>
///   <item>
///     A lawful basis for the declared purpose. Checked **before** the template
///     renders, because rendering pulls the subject's data into memory and a
///     refusal afterwards has already done the processing it was refusing.
///   </item>
///   <item>
///     The template and the supplied values agree — every placeholder valued,
///     no value unused.
///   </item>
///   <item>
///     The rendered content's classification is within the channel's ceiling.
///     This one is last because it needs the rendered result: the ceiling
///     applies to what would actually be sent, not to what the template
///     nominally is.
///   </item>
/// </list>
/// <para>
/// Nothing here logs a subject, an address or a body. The failures carry a
/// channel name, a ceiling and a purpose — enough to debug, nothing that turns
/// the log into the disclosure the controls just prevented.
/// </para>
/// </remarks>
public sealed class CommunicationDispatcher : ICommunicationDispatcher
{
    private readonly Dictionary<string, MessageTemplate> _templates;
    private readonly Dictionary<string, ICommunicationChannel> _channels;
    private readonly IConsentEvaluator _consent;
    private readonly ITenantContextAccessor _tenantAccessor;

    /// <summary>
    /// Composes the dispatcher over a fixed template and channel set.
    /// </summary>
    /// <param name="templates">The templates this deployment may send.</param>
    /// <param name="channels">The channels available, keyed by name.</param>
    /// <param name="consent">The lawful-basis evaluator.</param>
    /// <param name="tenantAccessor">Ambient tenant.</param>
    /// <exception cref="ArgumentNullException">Any dependency is null.</exception>
    /// <exception cref="ArgumentException">
    /// A template names a channel that was not supplied. Discovering that at
    /// send time would mean discovering it in production, so it is a
    /// composition-time failure instead (ADR-014).
    /// </exception>
    public CommunicationDispatcher(
        IReadOnlyList<MessageTemplate> templates,
        IReadOnlyList<ICommunicationChannel> channels,
        IConsentEvaluator consent,
        ITenantContextAccessor tenantAccessor)
    {
        Guard.NotNull(templates, nameof(templates));
        Guard.NotNull(channels, nameof(channels));

        _consent = Guard.NotNull(consent, nameof(consent));
        _tenantAccessor = Guard.NotNull(tenantAccessor, nameof(tenantAccessor));

        var channelMap = new Dictionary<string, ICommunicationChannel>(StringComparer.OrdinalIgnoreCase);
        foreach (ICommunicationChannel channel in channels)
        {
            channelMap[channel.ChannelName] = channel;
        }

        var templateMap = new Dictionary<string, MessageTemplate>(StringComparer.Ordinal);
        foreach (MessageTemplate template in templates)
        {
            if (!channelMap.ContainsKey(template.ChannelName))
            {
                throw new ArgumentException(
                    "A template names a channel that is not registered.", nameof(templates));
            }

            templateMap[template.TemplateId] = template;
        }

        _templates = templateMap;
        _channels = channelMap;
    }

    /// <inheritdoc />
    public async Task<Result<OutboundMessage>> SendAsync(
        SendRequest request,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(request, nameof(request));

        ITenantContext? tenant = _tenantAccessor.Current;
        if (tenant is null || tenant.TenantId == Guid.Empty)
        {
            return Result.Failure<OutboundMessage>(new Error(
                ErrorCodes.TenantScopeViolation,
                "The requested resource was not found.",
                ErrorCategory.NotFound));
        }

        if (!_templates.TryGetValue(request.TemplateId, out MessageTemplate? template))
        {
            return Result.Failure<OutboundMessage>(new Error(
                ErrorCodes.NotFound,
                "The requested resource was not found.",
                ErrorCategory.NotFound));
        }

        ICommunicationChannel channel = _channels[template.ChannelName];

        if (request.Recipient.Kind != channel.AddressKind)
        {
            return Result.Failure<OutboundMessage>(new Error(
                ErrorCodes.ValidationFailed,
                "The recipient address is not the kind this template's channel delivers to.",
                ErrorCategory.Validation));
        }

        // Consent first. A refusal after rendering has already processed the
        // subject's data, which is the thing the refusal was for.
        var processing = new ProcessingRequest(
            tenant.TenantId,
            request.SubjectToken,
            request.Purpose,
            ClassificationsOf(request.Values));

        Result<ConsentDecision> decision = await _consent
            .EvaluateAsync(processing, cancellationToken)
            .ConfigureAwait(false);

        if (decision.IsFailure)
        {
            return Result.Failure<OutboundMessage>(decision.Error!);
        }

        if (!decision.Value.IsPermitted)
        {
            return Result.Failure<OutboundMessage>(new Error(
                ErrorCodes.ConsentRequired,
                "No lawful basis for this purpose: " + request.Purpose,
                ErrorCategory.Compliance));
        }

        Result<RenderedMessage> rendered = template.Render(request.Values);
        if (rendered.IsFailure)
        {
            return Result.Failure<OutboundMessage>(rendered.Error!);
        }

        if (rendered.Value.Classification > channel.MaximumClassification)
        {
            return Result.Failure<OutboundMessage>(new Error(
                ErrorCodes.ChannelClassificationExceeded,
                "The " + channel.ChannelName + " channel carries at most "
                + channel.MaximumClassification + " content.",
                ErrorCategory.Compliance));
        }

        OutboundMessage message;
        try
        {
            message = new OutboundMessage(
                request.Recipient,
                rendered.Value.Subject,
                rendered.Value.Body,
                rendered.Value.Classification);
        }
        catch (ArgumentException)
        {
            // A substituted value put a control character into the subject.
            // The message is refused rather than sanitised, and the exception
            // detail is discarded because it would quote the value.
            return Result.Failure<OutboundMessage>(new Error(
                ErrorCodes.ValidationFailed,
                "The rendered message is not a valid message for this channel.",
                ErrorCategory.Validation));
        }

        Result sent = await channel.SendAsync(message, cancellationToken).ConfigureAwait(false);

        return sent.IsFailure ? Result.Failure<OutboundMessage>(sent.Error!) : message;
    }

    private static List<DataClassificationLevel> ClassificationsOf(
        IReadOnlyDictionary<string, TemplateValue> values)
    {
        var levels = new List<DataClassificationLevel>();
        foreach (KeyValuePair<string, TemplateValue> value in values)
        {
            if (!levels.Contains(value.Value.Classification))
            {
                levels.Add(value.Value.Classification);
            }
        }

        return levels;
    }
}
