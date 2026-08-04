using System;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Communication;

/// <summary>
/// A rendered message, ready to leave the platform.
/// </summary>
/// <remarks>
/// Construction validates the subject for header injection, because a subject
/// line is the other place a newline turns into an SMTP header. The body is
/// not checked the same way — a newline in a body is a newline.
/// </remarks>
public sealed class OutboundMessage
{
    /// <summary>Longest accepted subject.</summary>
    public const int MaxSubjectLength = 250;

    /// <summary>
    /// Assembles a message.
    /// </summary>
    /// <param name="recipient">The validated destination.</param>
    /// <param name="subject">The subject line. Ignored by channels that have none.</param>
    /// <param name="body">The rendered body.</param>
    /// <param name="classification">
    /// The effective classification of the rendered content — the highest of
    /// everything that went into it, not the template's nominal level.
    /// </param>
    /// <exception cref="ArgumentNullException">The recipient or body is null.</exception>
    /// <exception cref="ArgumentException">The subject is over-long or carries a control character.</exception>
    public OutboundMessage(
        MessageAddress recipient,
        string subject,
        string body,
        DataClassificationLevel classification)
    {
        Recipient = recipient ?? throw new ArgumentNullException(nameof(recipient));
        Body = body ?? throw new ArgumentNullException(nameof(body));

        if (subject is null)
        {
            throw new ArgumentNullException(nameof(subject));
        }

        if (subject.Length > MaxSubjectLength)
        {
            throw new ArgumentException("The subject exceeds the maximum length.", nameof(subject));
        }

        foreach (char c in subject)
        {
            if (char.IsControl(c))
            {
                throw new ArgumentException(
                    "A subject may not contain control characters. A newline here would start a new header.",
                    nameof(subject));
            }
        }

        Subject = subject;
        Classification = classification;
    }

    /// <summary>The validated destination.</summary>
    public MessageAddress Recipient { get; }

    /// <summary>The subject line.</summary>
    public string Subject { get; }

    /// <summary>The rendered body.</summary>
    public string Body { get; }

    /// <summary>The effective classification of the rendered content.</summary>
    public DataClassificationLevel Classification { get; }
}

/// <summary>
/// One delivery technology — SMTP pickup, a transactional email API, an SMS
/// gateway.
/// </summary>
/// <remarks>
/// <para>
/// The same split as storage: a channel does delivery, and
/// <see cref="ICommunicationDispatcher"/> does policy. A channel that enforced
/// consent itself would be one more place to get consent wrong.
/// </para>
/// <para>
/// The exception is <see cref="MaximumClassification"/>, which a channel must
/// declare because only the channel knows what its transport does. The
/// dispatcher enforces it; the channel states it.
/// </para>
/// </remarks>
public interface ICommunicationChannel
{
    /// <summary>A stable name — <c>Email</c>, <c>Sms</c>. Matched against a template's channel.</summary>
    string ChannelName { get; }

    /// <summary>The kind of address this channel delivers to.</summary>
    AddressKind AddressKind { get; }

    /// <summary>
    /// The highest classification this channel may carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For unencrypted email and for SMS this is
    /// <see cref="DataClassificationLevel.Internal"/> at most. The transport
    /// crosses infrastructure nobody in the deployment controls: carrier SMSCs
    /// store message text, and SMTP between organisations is opportunistically
    /// encrypted at best.
    /// </para>
    /// <para>
    /// This is why appointment reminders say "you have an appointment" and not
    /// "your oncology appointment". The ceiling is what makes that a property
    /// of the platform rather than a rule in a style guide.
    /// </para>
    /// </remarks>
    DataClassificationLevel MaximumClassification { get; }

    /// <summary>
    /// Delivers a message that has already passed policy.
    /// </summary>
    /// <param name="message">The rendered message.</param>
    /// <param name="cancellationToken">Cancels delivery.</param>
    /// <returns>Success, or a failure carrying no message content.</returns>
    Task<Result> SendAsync(OutboundMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// The policy layer for outbound communication: consent, classification
/// ceiling, template safety.
/// </summary>
public interface ICommunicationDispatcher
{
    /// <summary>
    /// Renders a template and, if every control permits it, sends the result.
    /// </summary>
    /// <param name="request">What to send, to whom, and why.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>
    /// The message as sent, or a failure. Failures carry
    /// <see cref="ErrorCodes.ConsentRequired"/> (no lawful basis for the
    /// declared purpose), <see cref="ErrorCodes.ChannelClassificationExceeded"/>
    /// (the rendered content is too sensitive for the transport),
    /// <see cref="ErrorCodes.ValidationFailed"/> (the template and the supplied
    /// values disagree), or <see cref="ErrorCodes.TenantScopeViolation"/>.
    /// </returns>
    Task<Result<OutboundMessage>> SendAsync(SendRequest request, CancellationToken cancellationToken);
}
