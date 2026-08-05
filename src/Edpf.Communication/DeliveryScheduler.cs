using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Communication;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;
using Edpf.Core.Time;

namespace Edpf.Communication;

/// <summary>Where a queued message has got to.</summary>
public enum DeliveryState
{
    /// <summary>Waiting for its due time.</summary>
    Queued = 0,

    /// <summary>Handed to a channel and accepted.</summary>
    Delivered = 1,

    /// <summary>Failed transiently and is waiting to be retried.</summary>
    Retrying = 2,

    /// <summary>Given up on. The reason is preserved.</summary>
    DeadLettered = 3,
}

/// <summary>One attempt, and what came of it.</summary>
/// <remarks>
/// The history is the delivery-tracking feature. "Did the patient get their
/// reminder" is answered from this, and the answer has to survive the process
/// that sent it.
/// </remarks>
public sealed class DeliveryAttempt
{
    /// <summary>
    /// Records an attempt.
    /// </summary>
    /// <param name="attemptNumber">Which attempt this was, from 1.</param>
    /// <param name="occurredUtc">When it ran.</param>
    /// <param name="succeeded">Whether the channel accepted the message.</param>
    /// <param name="errorCode">
    /// The stable error code when it failed. **A code, never a message** —
    /// a failure message can quote the content, and the tracking record is not
    /// a place for clinical text.
    /// </param>
    public DeliveryAttempt(int attemptNumber, DateTimeOffset occurredUtc, bool succeeded, string? errorCode)
    {
        AttemptNumber = attemptNumber;
        OccurredUtc = occurredUtc;
        Succeeded = succeeded;
        ErrorCode = errorCode;
    }

    /// <summary>Which attempt this was, from 1.</summary>
    public int AttemptNumber { get; }

    /// <summary>When it ran.</summary>
    public DateTimeOffset OccurredUtc { get; }

    /// <summary>Whether the channel accepted the message.</summary>
    public bool Succeeded { get; }

    /// <summary>The stable error code when it failed.</summary>
    public string? ErrorCode { get; }
}

/// <summary>A message waiting to be sent, with its history.</summary>
public sealed class QueuedDelivery
{
    private readonly List<DeliveryAttempt> _attempts = [];

    /// <summary>
    /// Queues a send.
    /// </summary>
    /// <param name="deliveryId">This delivery's id.</param>
    /// <param name="request">What to send.</param>
    /// <param name="tenantId">The owning tenant.</param>
    /// <param name="notBeforeUtc">The earliest instant it may be sent.</param>
    /// <param name="idempotencyKey">
    /// The caller's key, when supplied. Two enqueues with the same key produce
    /// one delivery.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="ArgumentException">The id is blank or the tenant is empty.</exception>
    public QueuedDelivery(
        string deliveryId,
        SendRequest request,
        Guid tenantId,
        DateTimeOffset notBeforeUtc,
        string? idempotencyKey = null)
    {
        DeliveryId = Guard.NotNullOrWhiteSpace(deliveryId, nameof(deliveryId));
        Request = Guard.NotNull(request, nameof(request));

        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException(
                "A queued delivery requires a tenant. A message with no tenant has no consent record and no "
                + "owner.",
                nameof(tenantId));
        }

        TenantId = tenantId;
        NotBeforeUtc = notBeforeUtc;
        IdempotencyKey = idempotencyKey;
        State = DeliveryState.Queued;
    }

    /// <summary>This delivery's id.</summary>
    public string DeliveryId { get; }

    /// <summary>What to send.</summary>
    public SendRequest Request { get; }

    /// <summary>The owning tenant.</summary>
    public Guid TenantId { get; }

    /// <summary>The earliest instant it may be sent.</summary>
    public DateTimeOffset NotBeforeUtc { get; private set; }

    /// <summary>The caller's idempotency key, when supplied.</summary>
    public string? IdempotencyKey { get; }

    /// <summary>Where it has got to.</summary>
    public DeliveryState State { get; private set; }

    /// <summary>Every attempt, oldest first.</summary>
    public IReadOnlyList<DeliveryAttempt> Attempts => _attempts;

    /// <summary>How many attempts have run.</summary>
    public int AttemptCount => _attempts.Count;

    internal void Record(DeliveryAttempt attempt, DeliveryState state, DateTimeOffset nextAttemptUtc)
    {
        _attempts.Add(attempt);
        State = state;
        NotBeforeUtc = nextAttemptUtc;
    }
}

/// <summary>Where queued deliveries live between attempts.</summary>
/// <remarks>
/// A seam because the queue must outlive the process. An in-memory
/// implementation loses every scheduled reminder on a restart, which is
/// acceptable in development and is not acceptable anywhere else.
/// </remarks>
public interface IDeliveryStore
{
    /// <summary>Adds a delivery, or returns the existing one for the same idempotency key.</summary>
    /// <param name="delivery">The delivery to queue.</param>
    /// <returns>The stored delivery — the existing one when the key was already used.</returns>
    QueuedDelivery Add(QueuedDelivery delivery);

    /// <summary>Returns deliveries that are due, oldest first.</summary>
    /// <param name="now">The current instant.</param>
    /// <param name="maxCount">The most to return.</param>
    /// <returns>The due deliveries.</returns>
    IReadOnlyList<QueuedDelivery> Due(DateTimeOffset now, int maxCount);

    /// <summary>Finds a delivery by id.</summary>
    /// <param name="deliveryId">The id.</param>
    /// <returns>The delivery, or null.</returns>
    QueuedDelivery? Find(string deliveryId);
}

/// <summary>
/// Exponential backoff with a cap.
/// </summary>
/// <remarks>
/// <para>
/// Deterministic, with no jitter, and that is a deliberate choice rather than
/// an omission. Jitter exists to stop a thundering herd of clients
/// synchronising on a shared outage; this queue is drained by one worker per
/// deployment, so there is no herd — and a deterministic schedule is one a
/// support engineer can predict when a clinician asks when the message will go
/// out.
/// </para>
/// </remarks>
public sealed class RetryPolicy
{
    /// <summary>
    /// Defines a policy.
    /// </summary>
    /// <param name="maxAttempts">How many attempts before dead-lettering.</param>
    /// <param name="firstDelay">The delay after the first failure.</param>
    /// <param name="maxDelay">The ceiling on the delay.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The attempt count is below one, or a delay is not positive.
    /// </exception>
    public RetryPolicy(int maxAttempts, TimeSpan firstDelay, TimeSpan maxDelay)
    {
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAttempts), "At least one attempt must be permitted.");
        }

        if (firstDelay <= TimeSpan.Zero || maxDelay < firstDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(firstDelay), "Delays must be positive and the ceiling must not be below the first delay.");
        }

        MaxAttempts = maxAttempts;
        FirstDelay = firstDelay;
        MaxDelay = maxDelay;
    }

    /// <summary>A reasonable default: five attempts over roughly half an hour.</summary>
    public static RetryPolicy Default { get; } =
        new(5, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15));

    /// <summary>How many attempts before dead-lettering.</summary>
    public int MaxAttempts { get; }

    /// <summary>The delay after the first failure.</summary>
    public TimeSpan FirstDelay { get; }

    /// <summary>The ceiling on the delay.</summary>
    public TimeSpan MaxDelay { get; }

    /// <summary>
    /// The delay before the given attempt number.
    /// </summary>
    /// <param name="attemptNumber">The attempt that just failed, from 1.</param>
    /// <returns>How long to wait before the next one.</returns>
    public TimeSpan DelayAfter(int attemptNumber)
    {
        if (attemptNumber < 1)
        {
            return FirstDelay;
        }

        // Doubling, computed in ticks and clamped before it can overflow.
        double factor = Math.Pow(2, Math.Min(attemptNumber - 1, 20));
        double ticks = FirstDelay.Ticks * factor;

        return ticks >= MaxDelay.Ticks ? MaxDelay : TimeSpan.FromTicks((long)ticks);
    }
}

/// <summary>
/// Queueing, scheduling, retry and delivery tracking for outbound messages.
/// </summary>
/// <remarks>
/// <para>
/// **The retry decision is derived from the error taxonomy, not restated
/// here.** A failure carrying <see cref="ErrorCategory.Transient"/> is retried;
/// anything else is dead-lettered on the first attempt. That single rule
/// covers the cases that matter: a consent refusal retried every thirty
/// seconds for an hour is not resilience, it is a compliance incident with a
/// scheduler attached — and a message above the channel's classification
/// ceiling will be above it just as much on the fifth attempt.
/// </para>
/// <para>
/// The same reasoning as the storage layer's protection table: a second
/// opinion about which failures are temporary would drift from the first, and
/// the gap between them would be the bug.
/// </para>
/// </remarks>
public sealed class DeliveryScheduler
{
    private readonly ICommunicationDispatcher _dispatcher;
    private readonly IDeliveryStore _store;
    private readonly RetryPolicy _retry;
    private readonly IClock _clock;
    private long _sequence;

    /// <summary>
    /// Composes the scheduler.
    /// </summary>
    /// <param name="dispatcher">The policy layer that actually sends.</param>
    /// <param name="store">Where deliveries live between attempts.</param>
    /// <param name="clock">Time source (Z.3 rule 4).</param>
    /// <param name="retry">The retry policy. Defaults to <see cref="RetryPolicy.Default"/>.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public DeliveryScheduler(
        ICommunicationDispatcher dispatcher,
        IDeliveryStore store,
        IClock clock,
        RetryPolicy? retry = null)
    {
        _dispatcher = Guard.NotNull(dispatcher, nameof(dispatcher));
        _store = Guard.NotNull(store, nameof(store));
        _clock = Guard.NotNull(clock, nameof(clock));
        _retry = retry ?? RetryPolicy.Default;
    }

    /// <summary>
    /// Queues a message for immediate or scheduled delivery.
    /// </summary>
    /// <param name="request">What to send.</param>
    /// <param name="tenantId">The owning tenant.</param>
    /// <param name="notBeforeUtc">
    /// The earliest instant to send. Null means as soon as the queue is
    /// drained. A time in the past sends at the next drain rather than being
    /// refused — a reminder scheduled for a moment that has passed is still
    /// wanted, just late.
    /// </param>
    /// <param name="idempotencyKey">
    /// Optional. Two enqueues with the same key produce one delivery, which is
    /// what stops a retried HTTP request from sending a patient two copies.
    /// </param>
    /// <returns>The queued delivery.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public QueuedDelivery Enqueue(
        SendRequest request,
        Guid tenantId,
        DateTimeOffset? notBeforeUtc = null,
        string? idempotencyKey = null)
    {
        Guard.NotNull(request, nameof(request));

        var delivery = new QueuedDelivery(
            NextId(),
            request,
            tenantId,
            StorableInstant.Normalize(notBeforeUtc ?? _clock.UtcNow),
            idempotencyKey);

        return _store.Add(delivery);
    }

    /// <summary>
    /// Sends every delivery that is due.
    /// </summary>
    /// <param name="maxCount">The most to attempt in this pass.</param>
    /// <param name="cancellationToken">Cancels the drain.</param>
    /// <returns>What the pass did.</returns>
    /// <remarks>
    /// Driven by a caller — a worker, a scheduler — rather than an internal
    /// timer, so a drain is testable without waiting and a paused host cannot
    /// silently stop sending while appearing healthy.
    /// </remarks>
    public async Task<DrainOutcome> DrainAsync(int maxCount, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        int delivered = 0;
        int retrying = 0;
        int deadLettered = 0;

        foreach (QueuedDelivery delivery in _store.Due(now, maxCount))
        {
            Result<OutboundMessage> sent = await _dispatcher
                .SendAsync(delivery.Request, cancellationToken)
                .ConfigureAwait(false);

            int attemptNumber = delivery.AttemptCount + 1;
            DateTimeOffset attemptedAt = StorableInstant.Normalize(_clock.UtcNow);

            if (sent.IsSuccess)
            {
                delivery.Record(
                    new DeliveryAttempt(attemptNumber, attemptedAt, succeeded: true, errorCode: null),
                    DeliveryState.Delivered,
                    delivery.NotBeforeUtc);

                delivered++;
                continue;
            }

            // Derived, not restated. Transient means try again; everything
            // else means the answer will not change.
            bool retryable = sent.Error!.Category == ErrorCategory.Transient;
            bool attemptsRemain = attemptNumber < _retry.MaxAttempts;

            if (retryable && attemptsRemain)
            {
                delivery.Record(
                    new DeliveryAttempt(attemptNumber, attemptedAt, succeeded: false, sent.Error.Code),
                    DeliveryState.Retrying,
                    StorableInstant.Normalize(attemptedAt.Add(_retry.DelayAfter(attemptNumber))));

                retrying++;
                continue;
            }

            delivery.Record(
                new DeliveryAttempt(attemptNumber, attemptedAt, succeeded: false, sent.Error.Code),
                DeliveryState.DeadLettered,
                delivery.NotBeforeUtc);

            deadLettered++;
        }

        return new DrainOutcome(delivered, retrying, deadLettered);
    }

    /// <summary>Reads a delivery's tracking record.</summary>
    /// <param name="deliveryId">The delivery id.</param>
    /// <returns>The delivery, or null when there is no such id.</returns>
    public QueuedDelivery? Track(string deliveryId)
        => _store.Find(Guard.NotNullOrWhiteSpace(deliveryId, nameof(deliveryId)));

    private string NextId()
        => "dlv-" + Interlocked.Increment(ref _sequence).ToString(CultureInfo.InvariantCulture);
}

/// <summary>What one drain pass did.</summary>
public sealed class DrainOutcome
{
    /// <summary>Records an outcome.</summary>
    /// <param name="delivered">How many were accepted by a channel.</param>
    /// <param name="retrying">How many failed transiently and will be retried.</param>
    /// <param name="deadLettered">How many were given up on.</param>
    public DrainOutcome(int delivered, int retrying, int deadLettered)
    {
        Delivered = delivered;
        Retrying = retrying;
        DeadLettered = deadLettered;
    }

    /// <summary>How many were accepted by a channel.</summary>
    public int Delivered { get; }

    /// <summary>How many failed transiently and will be retried.</summary>
    public int Retrying { get; }

    /// <summary>
    /// How many were given up on. Reported separately because "nothing was due"
    /// and "forty messages were abandoned" are different operational facts.
    /// </summary>
    public int DeadLettered { get; }
}

/// <summary>
/// Holds deliveries in process memory. Development and tests.
/// </summary>
/// <remarks>
/// **Loses every scheduled message on a restart**, which is stated plainly
/// because a queue that quietly forgets appointment reminders is worse than no
/// queue at all: the absence is invisible until a patient does not arrive.
/// </remarks>
public sealed class InMemoryDeliveryStore : IDeliveryStore
{
    private readonly List<QueuedDelivery> _deliveries = [];
    private readonly Dictionary<string, QueuedDelivery> _byIdempotencyKey = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public QueuedDelivery Add(QueuedDelivery delivery)
    {
        Guard.NotNull(delivery, nameof(delivery));

        if (delivery.IdempotencyKey is not null)
        {
            // Keyed by tenant as well as by the caller's key. Two tenants that
            // happen to choose "reminder-1" are not the same message.
            string key = delivery.TenantId.ToString("D") + "|" + delivery.IdempotencyKey;

            if (_byIdempotencyKey.TryGetValue(key, out QueuedDelivery? existing))
            {
                return existing;
            }

            _byIdempotencyKey[key] = delivery;
        }

        _deliveries.Add(delivery);
        return delivery;
    }

    /// <inheritdoc />
    public IReadOnlyList<QueuedDelivery> Due(DateTimeOffset now, int maxCount)
    {
        var due = new List<QueuedDelivery>();

        foreach (QueuedDelivery delivery in _deliveries)
        {
            if (due.Count >= maxCount)
            {
                break;
            }

            bool waiting = delivery.State is DeliveryState.Queued or DeliveryState.Retrying;

            if (waiting && delivery.NotBeforeUtc <= now)
            {
                due.Add(delivery);
            }
        }

        return due;
    }

    /// <inheritdoc />
    public QueuedDelivery? Find(string deliveryId)
    {
        foreach (QueuedDelivery delivery in _deliveries)
        {
            if (string.Equals(delivery.DeliveryId, deliveryId, StringComparison.Ordinal))
            {
                return delivery;
            }
        }

        return null;
    }
}
