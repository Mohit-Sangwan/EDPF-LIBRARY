using Edpf.Abstractions.Communication;
using Edpf.Abstractions.Primitives;
using Edpf.Communication;
using Edpf.UnitTests.TestDoubles;

namespace Edpf.UnitTests.Communication;

/// <summary>
/// Queueing, scheduling, retry and delivery tracking — the four capabilities
/// under the communication head that the dispatcher alone did not provide.
/// </summary>
public sealed class DeliverySchedulerTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private readonly FakeClock _clock = new();
    private readonly InMemoryDeliveryStore _store = new();

    private static SendRequest Reminder()
        => new(
            "appointment-reminder",
            MessageAddress.ForPhone("+441234567890"),
            new Dictionary<string, TemplateValue> { ["givenName"] = TemplateValue.Public("Alex") },
            "appointment-reminder",
            "subject-token-abc");

    private DeliveryScheduler CreateScheduler(
        ScriptedDispatcher dispatcher, RetryPolicy? retry = null)
        => new(dispatcher, _store, _clock, retry);

    // ── scheduling ────────────────────────────────────────────────────────

    [Fact]
    public async Task Enqueue_WithAFutureTime_DoesNotSendUntilItIsDue()
    {
        var dispatcher = new ScriptedDispatcher();
        DeliveryScheduler scheduler = CreateScheduler(dispatcher);

        scheduler.Enqueue(Reminder(), TenantA, _clock.UtcNow.AddHours(2));

        Assert.Equal(0, (await scheduler.DrainAsync(10, default)).Delivered);
        Assert.Equal(0, dispatcher.Sends);

        _clock.Advance(TimeSpan.FromHours(3));

        Assert.Equal(1, (await scheduler.DrainAsync(10, default)).Delivered);
    }

    [Fact]
    public async Task Enqueue_WithATimeInThePast_SendsAtTheNextDrain()
    {
        // A reminder scheduled for a moment that has passed is still wanted,
        // just late. Refusing it would discard the message entirely.
        var dispatcher = new ScriptedDispatcher();
        DeliveryScheduler scheduler = CreateScheduler(dispatcher);

        scheduler.Enqueue(Reminder(), TenantA, _clock.UtcNow.AddDays(-1));

        Assert.Equal(1, (await scheduler.DrainAsync(10, default)).Delivered);
    }

    [Fact]
    public async Task Drain_RespectsTheBatchLimit()
    {
        var dispatcher = new ScriptedDispatcher();
        DeliveryScheduler scheduler = CreateScheduler(dispatcher);

        for (int i = 0; i < 5; i++)
        {
            scheduler.Enqueue(Reminder(), TenantA);
        }

        Assert.Equal(2, (await scheduler.DrainAsync(2, default)).Delivered);
        Assert.Equal(2, dispatcher.Sends);
    }

    // ── the retry decision is derived, not restated ───────────────────────

    [Fact]
    public async Task TransientFailure_IsRetriedAfterABackoff()
    {
        var dispatcher = new ScriptedDispatcher
        {
            Failure = new Error(ErrorCodes.TransientFailure, "gateway down", ErrorCategory.Transient),
        };

        DeliveryScheduler scheduler = CreateScheduler(dispatcher);
        QueuedDelivery delivery = scheduler.Enqueue(Reminder(), TenantA);

        DrainOutcome first = await scheduler.DrainAsync(10, default);

        Assert.Equal(1, first.Retrying);
        Assert.Equal(DeliveryState.Retrying, delivery.State);

        // Not due yet: the backoff has to actually hold it back.
        Assert.Equal(0, (await scheduler.DrainAsync(10, default)).Retrying);

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, (await scheduler.DrainAsync(10, default)).Retrying);
    }

    [Theory]
    [InlineData(ErrorCategory.Validation)]
    [InlineData(ErrorCategory.Compliance)]
    [InlineData(ErrorCategory.Security)]
    [InlineData(ErrorCategory.NotFound)]
    public async Task NonTransientFailure_IsDeadLetteredOnTheFirstAttempt(ErrorCategory category)
    {
        // The rule that matters. A consent refusal retried every thirty
        // seconds for an hour is not resilience — it is a compliance incident
        // with a scheduler attached. And a message above the channel's
        // classification ceiling will be above it on the fifth attempt too.
        var dispatcher = new ScriptedDispatcher
        {
            Failure = new Error(ErrorCodes.ConsentRequired, "no lawful basis", category),
        };

        DeliveryScheduler scheduler = CreateScheduler(dispatcher);
        QueuedDelivery delivery = scheduler.Enqueue(Reminder(), TenantA);

        DrainOutcome outcome = await scheduler.DrainAsync(10, default);

        Assert.Equal(1, outcome.DeadLettered);
        Assert.Equal(DeliveryState.DeadLettered, delivery.State);
        Assert.Equal(1, dispatcher.Sends);
    }

    [Fact]
    public async Task RepeatedTransientFailure_IsDeadLetteredAfterTheAttemptLimit()
    {
        var dispatcher = new ScriptedDispatcher
        {
            Failure = new Error(ErrorCodes.TransientFailure, "gateway down", ErrorCategory.Transient),
        };

        var policy = new RetryPolicy(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        DeliveryScheduler scheduler = CreateScheduler(dispatcher, policy);
        QueuedDelivery delivery = scheduler.Enqueue(Reminder(), TenantA);

        for (int i = 0; i < 3; i++)
        {
            await scheduler.DrainAsync(10, default);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(DeliveryState.DeadLettered, delivery.State);
        Assert.Equal(3, delivery.AttemptCount);
    }

    [Fact]
    public void Backoff_DoublesAndThenHoldsAtTheCeiling()
    {
        var policy = new RetryPolicy(10, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15));

        Assert.Equal(TimeSpan.FromSeconds(30), policy.DelayAfter(1));
        Assert.Equal(TimeSpan.FromSeconds(60), policy.DelayAfter(2));
        Assert.Equal(TimeSpan.FromSeconds(120), policy.DelayAfter(3));
        Assert.Equal(TimeSpan.FromMinutes(15), policy.DelayAfter(9));

        // No overflow, however many attempts have run.
        Assert.Equal(TimeSpan.FromMinutes(15), policy.DelayAfter(1000));
    }

    [Fact]
    public void RetryPolicy_RefusesAnUnusableConfiguration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RetryPolicy(0, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RetryPolicy(3, TimeSpan.Zero, TimeSpan.FromSeconds(2)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RetryPolicy(3, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1)));
    }

    // ── idempotency ───────────────────────────────────────────────────────

    [Fact]
    public async Task Enqueue_TwiceWithTheSameKey_SendsOnce()
    {
        // What stops a retried HTTP request from texting a patient twice.
        var dispatcher = new ScriptedDispatcher();
        DeliveryScheduler scheduler = CreateScheduler(dispatcher);

        QueuedDelivery first = scheduler.Enqueue(Reminder(), TenantA, idempotencyKey: "reminder-1");
        QueuedDelivery second = scheduler.Enqueue(Reminder(), TenantA, idempotencyKey: "reminder-1");

        Assert.Equal(first.DeliveryId, second.DeliveryId);
        Assert.Equal(1, (await scheduler.DrainAsync(10, default)).Delivered);
        Assert.Equal(1, dispatcher.Sends);
    }

    [Fact]
    public async Task IdempotencyKeys_AreScopedPerTenant()
    {
        // Two tenants that both pick "reminder-1" are not sending the same
        // message, and collapsing them would drop one patient's reminder.
        var dispatcher = new ScriptedDispatcher();
        DeliveryScheduler scheduler = CreateScheduler(dispatcher);

        scheduler.Enqueue(Reminder(), TenantA, idempotencyKey: "reminder-1");
        scheduler.Enqueue(Reminder(), TenantB, idempotencyKey: "reminder-1");

        Assert.Equal(2, (await scheduler.DrainAsync(10, default)).Delivered);
    }

    // ── tracking ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Track_ReportsEveryAttemptWithACodeAndNoContent()
    {
        // "Did the patient get their reminder" is answered from here. A
        // failure MESSAGE could quote the body, so only the stable code is
        // kept.
        var dispatcher = new ScriptedDispatcher
        {
            Failure = new Error(ErrorCodes.TransientFailure, "Alex's number was rejected", ErrorCategory.Transient),
        };

        DeliveryScheduler scheduler = CreateScheduler(dispatcher);
        QueuedDelivery queued = scheduler.Enqueue(Reminder(), TenantA);

        await scheduler.DrainAsync(10, default);

        QueuedDelivery tracked = Assert.IsType<QueuedDelivery>(scheduler.Track(queued.DeliveryId));
        DeliveryAttempt attempt = Assert.Single(tracked.Attempts);

        Assert.Equal(1, attempt.AttemptNumber);
        Assert.False(attempt.Succeeded);
        Assert.Equal(ErrorCodes.TransientFailure, attempt.ErrorCode);
        Assert.Equal(0, attempt.OccurredUtc.UtcTicks % 10);
    }

    [Fact]
    public async Task Track_OfADeliveredMessage_ShowsTheSuccessfulAttempt()
    {
        var dispatcher = new ScriptedDispatcher();
        DeliveryScheduler scheduler = CreateScheduler(dispatcher);
        QueuedDelivery queued = scheduler.Enqueue(Reminder(), TenantA);

        await scheduler.DrainAsync(10, default);

        QueuedDelivery tracked = scheduler.Track(queued.DeliveryId)!;

        Assert.Equal(DeliveryState.Delivered, tracked.State);
        Assert.True(Assert.Single(tracked.Attempts).Succeeded);
    }

    [Fact]
    public void Track_OfAnUnknownId_IsNull()
        => Assert.Null(CreateScheduler(new ScriptedDispatcher()).Track("dlv-nope"));

    [Fact]
    public async Task DeliveredMessage_IsNotSentAgainOnTheNextDrain()
    {
        var dispatcher = new ScriptedDispatcher();
        DeliveryScheduler scheduler = CreateScheduler(dispatcher);

        scheduler.Enqueue(Reminder(), TenantA);

        await scheduler.DrainAsync(10, default);
        _clock.Advance(TimeSpan.FromHours(1));
        await scheduler.DrainAsync(10, default);

        Assert.Equal(1, dispatcher.Sends);
    }

    [Fact]
    public void QueuedDelivery_RequiresATenant()
    {
        // A message with no tenant has no consent record and no owner.
        Assert.Throws<ArgumentException>(
            () => new QueuedDelivery("dlv-1", Reminder(), Guid.Empty, DateTimeOffset.UtcNow));
    }

    private sealed class ScriptedDispatcher : ICommunicationDispatcher
    {
        public int Sends { get; private set; }

        public Error? Failure { get; set; }

        public Task<Result<OutboundMessage>> SendAsync(
            SendRequest request, CancellationToken cancellationToken)
        {
            Sends++;

            if (Failure is not null)
            {
                return Task.FromResult(Result.Failure<OutboundMessage>(Failure));
            }

            return Task.FromResult(Result<OutboundMessage>.FromValue(new OutboundMessage(
                request.Recipient, "Reminder", "body", DataClassificationLevel.Internal)));
        }
    }
}
