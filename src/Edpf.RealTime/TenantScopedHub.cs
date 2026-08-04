using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.RealTime;
using Edpf.Abstractions.Tenancy;
using Edpf.Core.Guards;
using Edpf.Core.Time;

namespace Edpf.RealTime;

/// <summary>
/// The policy layer for real-time delivery: tenancy, scope, classification
/// ceiling, and the acknowledgement rule.
/// </summary>
/// <remarks>
/// <para>
/// A socket is a read that stays open. Everything the query path enforces on a
/// request has to hold here too, and none of it is enforced by the transport —
/// SignalR will happily fan a message out to every connection in a process,
/// across tenants, because it has no idea tenants exist.
/// </para>
/// <para>
/// **The acknowledgement rule is the part that is not infrastructure.** A
/// channel declared <see cref="DeliveryGuarantee.RequiresAcknowledgement"/>
/// holds its message until a subscriber acknowledges it and escalates when the
/// deadline passes. A critical laboratory result pushed to a browser that had
/// closed is not delivered, and a platform that reports it as sent has told a
/// clinician something false about a patient.
/// </para>
/// </remarks>
public sealed class TenantScopedHub : IRealTimeHub
{
    private readonly Dictionary<string, RealTimeChannel> _channels;
    private readonly ISubscriberTransport _transport;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly IClock _clock;
    private readonly IAlertEscalator? _escalator;

    // Keyed by tenant + channel, so a fan-out cannot reach another tenant's
    // connections even if a channel name collides.
    private readonly Dictionary<string, List<string>> _subscriptions = new(StringComparer.Ordinal);

    private readonly List<PendingAcknowledgement> _pending = [];

    private long _sequence;

    /// <summary>
    /// Composes the hub.
    /// </summary>
    /// <param name="channels">Every channel this deployment publishes.</param>
    /// <param name="transport">The push transport.</param>
    /// <param name="tenantAccessor">Ambient tenant.</param>
    /// <param name="clock">Time source (Z.3 rule 4).</param>
    /// <param name="escalator">
    /// Where unacknowledged critical messages go. Required if any channel
    /// declares <see cref="DeliveryGuarantee.RequiresAcknowledgement"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    /// <exception cref="ArgumentException">
    /// A channel requires acknowledgement and no escalator was supplied. That
    /// combination is a channel which promises a message will not be lost and
    /// has nowhere to send it when it is — a promise with no mechanism, which
    /// is worse than best-effort honestly labelled.
    /// </exception>
    public TenantScopedHub(
        IReadOnlyList<RealTimeChannel> channels,
        ISubscriberTransport transport,
        ITenantContextAccessor tenantAccessor,
        IClock clock,
        IAlertEscalator? escalator = null)
    {
        Guard.NotNull(channels, nameof(channels));
        _transport = Guard.NotNull(transport, nameof(transport));
        _tenantAccessor = Guard.NotNull(tenantAccessor, nameof(tenantAccessor));
        _clock = Guard.NotNull(clock, nameof(clock));
        _escalator = escalator;

        _channels = new Dictionary<string, RealTimeChannel>(StringComparer.Ordinal);
        foreach (RealTimeChannel channel in channels)
        {
            if (channel.Delivery == DeliveryGuarantee.RequiresAcknowledgement && escalator is null)
            {
                throw new ArgumentException(
                    "A channel requires acknowledgement but no escalator is configured. A guarantee with no "
                    + "mechanism behind it is worse than best-effort honestly labelled.",
                    nameof(escalator));
            }

            _channels[channel.Name] = channel;
        }
    }

    /// <summary>How long an unacknowledged critical message waits before escalating.</summary>
    public TimeSpan AcknowledgementDeadline { get; set; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public Result Subscribe(string channelName, string connectionId, IReadOnlyList<string> heldScopes)
    {
        Guard.NotNullOrWhiteSpace(channelName, nameof(channelName));
        Guard.NotNullOrWhiteSpace(connectionId, nameof(connectionId));
        Guard.NotNull(heldScopes, nameof(heldScopes));

        ITenantContext? tenant = _tenantAccessor.Current;
        if (tenant is null || tenant.TenantId == Guid.Empty)
        {
            return Result.Failure(NotFound());
        }

        // A channel the caller may not join and a channel that does not exist
        // fail identically. Otherwise a client could enumerate the deployment's
        // channel names by watching which refusals differ.
        if (!_channels.TryGetValue(channelName, out RealTimeChannel? channel)
            || !Holds(heldScopes, channel.RequiredScope))
        {
            return Result.Failure(NotFound());
        }

        string key = KeyFor(tenant.TenantId, channelName);
        if (!_subscriptions.TryGetValue(key, out List<string>? connections))
        {
            connections = [];
            _subscriptions[key] = connections;
        }

        if (!connections.Contains(connectionId))
        {
            connections.Add(connectionId);
        }

        return Result.Success();
    }

    /// <summary>Removes a connection from every channel in the ambient tenant.</summary>
    /// <param name="connectionId">The connection that went away.</param>
    /// <remarks>
    /// Called on disconnect. A connection left in the table is a connection the
    /// hub still counts as a subscriber, which is how an acknowledgement
    /// deadline gets satisfied by somebody who is not there.
    /// </remarks>
    public void Disconnect(string connectionId)
    {
        Guard.NotNullOrWhiteSpace(connectionId, nameof(connectionId));

        foreach (List<string> connections in _subscriptions.Values)
        {
            connections.Remove(connectionId);
        }
    }

    /// <inheritdoc />
    public async Task<Result<int>> PublishAsync(
        string channelName,
        string payload,
        DataClassificationLevel classification,
        CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(channelName, nameof(channelName));
        Guard.NotNull(payload, nameof(payload));

        ITenantContext? tenant = _tenantAccessor.Current;
        if (tenant is null || tenant.TenantId == Guid.Empty)
        {
            return Result.Failure<int>(NotFound());
        }

        if (!_channels.TryGetValue(channelName, out RealTimeChannel? channel))
        {
            return Result.Failure<int>(NotFound());
        }

        if (classification > channel.MaximumClassification)
        {
            return Result.Failure<int>(new Error(
                ErrorCodes.ChannelClassificationExceeded,
                "The " + channel.Name + " channel carries at most " + channel.MaximumClassification + " content.",
                ErrorCategory.Compliance));
        }

        var message = new RealTimeMessage(
            NextMessageId(),
            channel.Name,
            payload,
            classification,
            StorableInstant.Normalize(_clock.UtcNow));

        _subscriptions.TryGetValue(KeyFor(tenant.TenantId, channelName), out List<string>? connections);
        int delivered = 0;

        if (connections is not null)
        {
            foreach (string connectionId in connections)
            {
                Result pushed = await _transport
                    .PushAsync(connectionId, message, cancellationToken)
                    .ConfigureAwait(false);

                if (pushed.IsSuccess)
                {
                    delivered++;
                }
            }
        }

        if (channel.Delivery == DeliveryGuarantee.RequiresAcknowledgement)
        {
            // Recorded even when it reached somebody. "Pushed" is not
            // "read" — the socket accepted the frame, which says nothing
            // about whether a human saw it.
            _pending.Add(new PendingAcknowledgement(
                message, tenant.TenantId, _clock.UtcNow.Add(AcknowledgementDeadline)));
        }

        return delivered;
    }

    /// <summary>
    /// Records that a subscriber acknowledged a message.
    /// </summary>
    /// <param name="messageId">The message acknowledged.</param>
    /// <returns>
    /// Success, or not-found when no such message is pending — which includes
    /// one already escalated.
    /// </returns>
    public Result Acknowledge(string messageId)
    {
        Guard.NotNullOrWhiteSpace(messageId, nameof(messageId));

        ITenantContext? tenant = _tenantAccessor.Current;
        if (tenant is null)
        {
            return Result.Failure(NotFound());
        }

        for (int i = 0; i < _pending.Count; i++)
        {
            if (string.Equals(_pending[i].Message.MessageId, messageId, StringComparison.Ordinal)
                && _pending[i].TenantId == tenant.TenantId)
            {
                _pending.RemoveAt(i);
                return Result.Success();
            }
        }

        return Result.Failure(NotFound());
    }

    /// <summary>
    /// Escalates every acknowledgement whose deadline has passed.
    /// </summary>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    /// <returns>How many were escalated.</returns>
    /// <remarks>
    /// Driven by a scheduler rather than a timer inside the hub, so the sweep
    /// is testable without waiting and so a paused host does not silently stop
    /// escalating.
    /// </remarks>
    public async Task<int> EscalateOverdueAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        var overdue = new List<PendingAcknowledgement>();

        foreach (PendingAcknowledgement pending in _pending)
        {
            if (pending.DeadlineUtc <= now)
            {
                overdue.Add(pending);
            }
        }

        foreach (PendingAcknowledgement pending in overdue)
        {
            await _escalator!
                .EscalateAsync(pending.Message, pending.TenantId, cancellationToken)
                .ConfigureAwait(false);

            _pending.Remove(pending);
        }

        return overdue.Count;
    }

    /// <summary>Messages still waiting for an acknowledgement.</summary>
    public int PendingAcknowledgementCount => _pending.Count;

    private string NextMessageId()
        => "rt-" + Interlocked.Increment(ref _sequence).ToString(CultureInfo.InvariantCulture);

    private static string KeyFor(Guid tenantId, string channelName)
        => tenantId.ToString("D") + "|" + channelName;

    private static bool Holds(IReadOnlyList<string> heldScopes, string required)
    {
        for (int i = 0; i < heldScopes.Count; i++)
        {
            if (string.Equals(heldScopes[i], required, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static Error NotFound() => new(
        ErrorCodes.NotFound,
        "The requested resource was not found.",
        ErrorCategory.NotFound);

    private sealed class PendingAcknowledgement(RealTimeMessage message, Guid tenantId, DateTimeOffset deadlineUtc)
    {
        internal RealTimeMessage Message { get; } = message;

        internal Guid TenantId { get; } = tenantId;

        internal DateTimeOffset DeadlineUtc { get; } = deadlineUtc;
    }
}

/// <summary>
/// Records pushes in memory. Tests, and the development loop where a real
/// socket server is more trouble than the feature being built.
/// </summary>
public sealed class RecordingTransport : ISubscriberTransport
{
    private readonly List<(string ConnectionId, RealTimeMessage Message)> _pushes = [];

    /// <inheritdoc />
    public string TransportName => "Recording";

    /// <summary>Every push attempted, in order.</summary>
    public IReadOnlyList<(string ConnectionId, RealTimeMessage Message)> Pushes => _pushes;

    /// <summary>Connections that should fail — a closed browser tab, in effect.</summary>
    public HashSet<string> FailingConnections { get; } = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<Result> PushAsync(
        string connectionId, RealTimeMessage message, CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(connectionId, nameof(connectionId));
        Guard.NotNull(message, nameof(message));

        if (FailingConnections.Contains(connectionId))
        {
            return Task.FromResult(Result.Failure(new Error(
                ErrorCodes.TransientFailure, "The connection is gone.", ErrorCategory.Transient)));
        }

        _pushes.Add((connectionId, message));
        return Task.FromResult(Result.Success());
    }
}
