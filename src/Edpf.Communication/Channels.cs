using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Communication;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Communication;

/// <summary>
/// Writes RFC 5322 messages into a local pickup directory for an MTA to
/// collect.
/// </summary>
/// <remarks>
/// <para>
/// A real, dependency-free email channel. Pickup directories are how IIS SMTP,
/// Postfix's maildrop and most sidecar relays accept mail, and handing a
/// message to a local agent is a more defensible deployment than dialling
/// remote SMTP from application code anyway: retry, queueing, DKIM signing and
/// TLS policy belong to the MTA.
/// </para>
/// <para>
/// **Network channels — SendGrid, SES, Twilio — ship as optional packages**
/// so the core graph carries no vendor client (ADR-009). They implement the
/// same six-line interface and inherit the dispatcher's controls; what they do
/// not inherit is a ceiling, which each must declare for itself.
/// </para>
/// </remarks>
public sealed class PickupDirectoryEmailChannel : ICommunicationChannel
{
    private readonly string _pickupDirectory;
    private readonly MessageAddress _sender;

    /// <summary>
    /// Points the channel at a pickup directory.
    /// </summary>
    /// <param name="pickupDirectory">Where to drop <c>.eml</c> files. Created if absent.</param>
    /// <param name="sender">The envelope sender.</param>
    /// <param name="maximumClassification">
    /// The ceiling for this deployment. Defaults to
    /// <see cref="DataClassificationLevel.Internal"/>, which is the right
    /// answer for mail leaving the organisation.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="pickupDirectory"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="sender"/> is null.</exception>
    /// <remarks>
    /// The ceiling is a constructor parameter rather than a constant because a
    /// deployment that has verified end-to-end encryption to a known internal
    /// relay may legitimately raise it. Making that an explicit argument means
    /// raising it is a decision somebody made and can be asked about.
    /// </remarks>
    public PickupDirectoryEmailChannel(
        string pickupDirectory,
        MessageAddress sender,
        DataClassificationLevel maximumClassification = DataClassificationLevel.Internal)
    {
        Guard.NotNullOrWhiteSpace(pickupDirectory, nameof(pickupDirectory));
        _sender = Guard.NotNull(sender, nameof(sender));

        if (sender.Kind != AddressKind.Email)
        {
            throw new ArgumentException("The sender must be an email address.", nameof(sender));
        }

        _pickupDirectory = Path.GetFullPath(pickupDirectory);
        Directory.CreateDirectory(_pickupDirectory);
        MaximumClassification = maximumClassification;
    }

    /// <inheritdoc />
    public string ChannelName => "Email";

    /// <inheritdoc />
    public AddressKind AddressKind => AddressKind.Email;

    /// <inheritdoc />
    public DataClassificationLevel MaximumClassification { get; }

    /// <inheritdoc />
    public async Task<Result> SendAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        Guard.NotNull(message, nameof(message));

        var builder = new StringBuilder();
        builder.Append("From: ").Append(_sender.Value).Append("\r\n");
        builder.Append("To: ").Append(message.Recipient.Value).Append("\r\n");
        builder.Append("Subject: ").Append(message.Subject).Append("\r\n");
        builder.Append("MIME-Version: 1.0\r\n");
        builder.Append("Content-Type: text/plain; charset=utf-8\r\n");
        builder.Append("\r\n");
        builder.Append(message.Body);

        // The header block above is assembled by concatenation with no
        // escaping, which is only safe because MessageAddress and
        // OutboundMessage already refused every control character. That is the
        // trade: validate once at construction, and the twenty places that
        // consume the value need no defence of their own.
        string file = Path.Combine(
            _pickupDirectory,
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".eml");

        try
        {
            await File.WriteAllTextAsync(file, builder.ToString(), cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (IOException)
        {
            return Result.Failure(new Error(
                ErrorCodes.IntegrationFailed,
                "The message could not be handed to the local mail agent.",
                ErrorCategory.Transient));
        }
        catch (UnauthorizedAccessException)
        {
            return Result.Failure(new Error(
                ErrorCodes.IntegrationFailed,
                "The message could not be handed to the local mail agent.",
                ErrorCategory.Transient));
        }
    }
}

/// <summary>
/// Keeps sent messages in memory. Tests, and the local development loop where
/// sending real mail to real people is the failure mode.
/// </summary>
public sealed class RecordingChannel : ICommunicationChannel
{
    private readonly List<OutboundMessage> _sent = [];

    /// <summary>
    /// Declares a recording channel.
    /// </summary>
    /// <param name="channelName">The name templates will address.</param>
    /// <param name="addressKind">The address kind this channel accepts.</param>
    /// <param name="maximumClassification">The ceiling to enforce.</param>
    /// <exception cref="ArgumentException"><paramref name="channelName"/> is blank.</exception>
    public RecordingChannel(
        string channelName,
        AddressKind addressKind,
        DataClassificationLevel maximumClassification)
    {
        ChannelName = Guard.NotNullOrWhiteSpace(channelName, nameof(channelName));
        AddressKind = addressKind;
        MaximumClassification = maximumClassification;
    }

    /// <summary>An SMS channel with the ceiling SMS actually warrants.</summary>
    /// <remarks>
    /// Carrier SMSCs store message text, handsets show it on a lock screen, and
    /// the transport has no encryption to speak of. <c>Internal</c> is
    /// generous.
    /// </remarks>
    public static RecordingChannel Sms()
        => new("Sms", AddressKind.Phone, DataClassificationLevel.Internal);

    /// <inheritdoc />
    public string ChannelName { get; }

    /// <inheritdoc />
    public AddressKind AddressKind { get; }

    /// <inheritdoc />
    public DataClassificationLevel MaximumClassification { get; }

    /// <summary>Everything this channel was asked to send.</summary>
    public IReadOnlyList<OutboundMessage> Sent => _sent;

    /// <inheritdoc />
    public Task<Result> SendAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        Guard.NotNull(message, nameof(message));
        cancellationToken.ThrowIfCancellationRequested();

        _sent.Add(message);
        return Task.FromResult(Result.Success());
    }
}
