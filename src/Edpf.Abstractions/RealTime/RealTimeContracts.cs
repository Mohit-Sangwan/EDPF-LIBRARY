using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.RealTime;

/// <summary>How a channel behaves when nobody is listening.</summary>
/// <remarks>
/// The distinction is clinical rather than technical. A dashboard tile that
/// nobody is watching may be dropped; a critical laboratory result may not.
/// Encoding it in the channel means the decision is made once, by whoever
/// declared the channel, rather than at each publish site.
/// </remarks>
public enum DeliveryGuarantee
{
    /// <summary>
    /// Dropped if no subscriber is connected. Correct for live tiles, presence
    /// and progress: a value nobody saw is superseded a second later anyway.
    /// </summary>
    BestEffort = 0,

    /// <summary>
    /// Held until a subscriber acknowledges it, and escalated if none does.
    /// **The only correct setting for anything a clinician must act on.**
    /// </summary>
    RequiresAcknowledgement = 1,
}

/// <summary>
/// A named real-time channel with a declared ceiling and delivery guarantee.
/// </summary>
/// <remarks>
/// The same shape as a communication channel and an inference provider, and
/// deliberately so: everything that can carry data out of the platform declares
/// what it may carry, in one type, checked in one place.
/// </remarks>
public sealed class RealTimeChannel
{
    /// <summary>
    /// Declares a channel.
    /// </summary>
    /// <param name="name">The channel name subscribers address.</param>
    /// <param name="maximumClassification">The highest classification it may carry.</param>
    /// <param name="delivery">What happens when nobody is listening.</param>
    /// <param name="requiredScope">
    /// The authorization scope a subscriber must hold. Required — there is no
    /// unscoped channel, because a real-time feed is a read and reading is
    /// authorized (ADR-031).
    /// </param>
    /// <exception cref="ArgumentException">The name or required scope is blank.</exception>
    public RealTimeChannel(
        string name,
        DataClassificationLevel maximumClassification,
        DeliveryGuarantee delivery,
        string requiredScope)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A channel requires a name.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(requiredScope))
        {
            throw new ArgumentException(
                "A channel requires a scope. An unscoped real-time feed is an unauthenticated read that "
                + "happens to arrive over a socket.",
                nameof(requiredScope));
        }

        Name = name;
        MaximumClassification = maximumClassification;
        Delivery = delivery;
        RequiredScope = requiredScope;
    }

    /// <summary>The channel name subscribers address.</summary>
    public string Name { get; }

    /// <summary>The highest classification this channel may carry.</summary>
    public DataClassificationLevel MaximumClassification { get; }

    /// <summary>What happens when nobody is listening.</summary>
    public DeliveryGuarantee Delivery { get; }

    /// <summary>The authorization scope a subscriber must hold.</summary>
    public string RequiredScope { get; }
}

/// <summary>One message on its way to subscribers.</summary>
public sealed class RealTimeMessage
{
    /// <summary>
    /// Assembles a message.
    /// </summary>
    /// <param name="messageId">A unique id, used for acknowledgement.</param>
    /// <param name="channelName">The channel it belongs to.</param>
    /// <param name="payload">The body. Never logged.</param>
    /// <param name="classification">What the payload is.</param>
    /// <param name="publishedUtc">When it was published.</param>
    /// <exception cref="ArgumentException">The id or channel name is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is null.</exception>
    public RealTimeMessage(
        string messageId,
        string channelName,
        string payload,
        DataClassificationLevel classification,
        DateTimeOffset publishedUtc)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            throw new ArgumentException("A message requires an id to acknowledge against.", nameof(messageId));
        }

        if (string.IsNullOrWhiteSpace(channelName))
        {
            throw new ArgumentException("A message requires a channel.", nameof(channelName));
        }

        MessageId = messageId;
        ChannelName = channelName;
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        Classification = classification;
        PublishedUtc = publishedUtc;
    }

    /// <summary>A unique id, used for acknowledgement.</summary>
    public string MessageId { get; }

    /// <summary>The channel it belongs to.</summary>
    public string ChannelName { get; }

    /// <summary>The body. Never logged, never placed in an error message.</summary>
    public string Payload { get; }

    /// <summary>What the payload is.</summary>
    public DataClassificationLevel Classification { get; }

    /// <summary>When it was published.</summary>
    public DateTimeOffset PublishedUtc { get; }
}

/// <summary>
/// The transport a hub pushes through — SignalR, raw WebSocket, server-sent
/// events, or a test double.
/// </summary>
/// <remarks>
/// **A transport is not a hub**, the same way a storage backend is not a store.
/// It cannot be registered in place of one, so the tenant and classification
/// checks cannot be bypassed by a plausible line of composition code.
/// </remarks>
public interface ISubscriberTransport
{
    /// <summary>A stable name for diagnostics.</summary>
    string TransportName { get; }

    /// <summary>
    /// Pushes to one connection.
    /// </summary>
    /// <param name="connectionId">The connection to push to.</param>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancels the push.</param>
    /// <returns>Success, or a failure carrying no payload.</returns>
    Task<Result> PushAsync(string connectionId, RealTimeMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Where an unacknowledged critical message goes when the real-time path has
/// failed to deliver it.
/// </summary>
/// <remarks>
/// The escalation path must not be the same transport that just failed. In
/// practice it is a pager, an SMS, or a ward telephone — something whose
/// failure mode is uncorrelated with a dropped WebSocket.
/// </remarks>
public interface IAlertEscalator
{
    /// <summary>
    /// Escalates a message no subscriber acknowledged.
    /// </summary>
    /// <param name="message">The unacknowledged message.</param>
    /// <param name="tenantId">The tenant it belongs to.</param>
    /// <param name="cancellationToken">Cancels the escalation.</param>
    /// <returns>Success, or a failure.</returns>
    Task<Result> EscalateAsync(RealTimeMessage message, Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>The policy layer for real-time delivery.</summary>
public interface IRealTimeHub
{
    /// <summary>
    /// Subscribes a connection to a channel under the ambient tenant.
    /// </summary>
    /// <param name="channelName">The channel to join.</param>
    /// <param name="connectionId">The transport's connection id.</param>
    /// <param name="heldScopes">The scopes the subscriber holds.</param>
    /// <returns>
    /// Success, or a failure. A missing scope and a non-existent channel
    /// present identically — a subscription attempt is not a channel-discovery
    /// oracle.
    /// </returns>
    Result Subscribe(string channelName, string connectionId, IReadOnlyList<string> heldScopes);

    /// <summary>
    /// Publishes to a channel's subscribers within the ambient tenant.
    /// </summary>
    /// <param name="channelName">The channel.</param>
    /// <param name="payload">The body.</param>
    /// <param name="classification">What the payload is.</param>
    /// <param name="cancellationToken">Cancels the publish.</param>
    /// <returns>
    /// How many subscribers it reached, or a failure with
    /// <see cref="ErrorCodes.ChannelClassificationExceeded"/> when the payload
    /// is too sensitive for the channel.
    /// </returns>
    Task<Result<int>> PublishAsync(
        string channelName,
        string payload,
        DataClassificationLevel classification,
        CancellationToken cancellationToken);
}
