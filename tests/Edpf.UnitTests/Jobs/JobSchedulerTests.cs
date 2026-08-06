using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Tenancy;
using Edpf.Core.Tenancy;
using Edpf.Jobs;
using Edpf.UnitTests.TestDoubles;

namespace Edpf.UnitTests.Jobs;

/// <summary>
/// Background processing. The interesting failures are not "did it run" but
/// "did it run twice" and "did it run seventy-two times after an outage".
/// </summary>
public sealed class JobSchedulerTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Unknown = Guid.Parse("dddddddd-0000-0000-0000-000000000004");

    private readonly TenantContextAccessor _tenants = new();
    private readonly InMemoryJobStateStore _state = new();
    private readonly FakeClock _clock = new();

    private static CronSchedule Cron(string expression) => CronSchedule.Parse(expression).Value;

    // ── cron parsing ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("* * * *")]
    [InlineData("* * * * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 0 * *")]
    [InlineData("* * * 13 *")]
    [InlineData("* * * * 7")]
    [InlineData("30-10 * * * *")]
    [InlineData("*/0 * * * *")]
    public void Parse_RefusesAnInvalidExpression(string expression)
        => Assert.True(CronSchedule.Parse(expression).IsFailure);

    [Fact]
    public void NextOccurrence_IsStrictlyAfterTheGivenInstant()
    {
        // An occurrence exactly at the instant would make a scheduler that
        // records "last run at T" and asks for "next after T" run the same
        // minute forever.
        CronSchedule every = Cron("* * * * *");
        var now = new DateTimeOffset(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal(now.AddMinutes(1), every.NextOccurrence(now));
    }

    [Fact]
    public void NextOccurrence_HandlesStepsAndRanges()
    {
        CronSchedule quarterHour = Cron("*/15 9-17 * * *");
        var morning = new DateTimeOffset(2026, 8, 5, 9, 1, 0, TimeSpan.Zero);

        Assert.Equal(
            new DateTimeOffset(2026, 8, 5, 9, 15, 0, TimeSpan.Zero),
            quarterHour.NextOccurrence(morning));
    }

    [Fact]
    public void NextOccurrence_SkipsOutsideTheHourRange()
    {
        CronSchedule officeHours = Cron("0 9-17 * * *");
        var evening = new DateTimeOffset(2026, 8, 5, 18, 30, 0, TimeSpan.Zero);

        Assert.Equal(
            new DateTimeOffset(2026, 8, 6, 9, 0, 0, TimeSpan.Zero),
            officeHours.NextOccurrence(evening));
    }

    [Fact]
    public void BothDayFieldsRestricted_AreOredNotAnded()
    {
        // The cron oddity. "0 0 1 * 1" is the first of the month AND every
        // Monday, not Mondays that fall on the first. Anding makes a monthly
        // job nearly never fire.
        CronSchedule schedule = Cron("0 0 1 * 1");

        // 3 August 2026 is a Monday and not the first.
        Assert.True(schedule.Matches(new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero)));

        // 1 August 2026 is a Saturday and is the first.
        Assert.True(schedule.Matches(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)));

        // 4 August is neither.
        Assert.False(schedule.Matches(new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void OnlyOneDayFieldRestricted_BehavesNormally()
    {
        CronSchedule mondays = Cron("0 0 * * 1");

        Assert.True(mondays.Matches(new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero)));
        Assert.False(mondays.Matches(new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void ImpossibleSchedule_ReturnsNullRatherThanThrowing()
    {
        // "31 February" parses and never occurs. A configuration mistake to
        // report, not a crash at the first tick.
        CronSchedule never = Cron("0 0 31 2 *");

        Assert.Null(never.NextOccurrence(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    // ── the scheduler ─────────────────────────────────────────────────────

    private JobScheduler CreateScheduler(
        IJobHandler handler,
        CatchUpPolicy catchUp = CatchUpPolicy.RunOnce,
        string cron = "*/5 * * * *",
        TimeSpan? timeout = null)
        => new(
            [new JobDefinition("nightly", Cron(cron), handler.HandlerName, catchUp, 3, timeout)],
            [handler],
            _state,
            _tenants,
            new SingleTenantStore(TenantA),
            _clock);

    [Fact]
    public void Scheduler_RefusesAJobWhoseHandlerIsNotRegistered()
    {
        // A job with no handler never runs, and nothing else would report
        // that.
        Assert.Throws<ArgumentException>(() => new JobScheduler(
            [new JobDefinition("orphan", Cron("* * * * *"), "missing")],
            [],
            _state,
            _tenants,
            new SingleTenantStore(TenantA),
            _clock));
    }

    [Fact]
    public async Task Tick_RunsADueJobAndPushesTheTenant()
    {
        var handler = new RecordingHandler(_tenants);
        JobScheduler scheduler = CreateScheduler(handler);

        // The first tick anchors and runs nothing; the second is due.
        await scheduler.TickAsync(TenantA, default);
        _clock.Advance(TimeSpan.FromMinutes(6));

        TickOutcome outcome = (await scheduler.TickAsync(TenantA, default)).Value;

        Assert.Equal(1, outcome.Ran);
        Assert.Equal(TenantA, handler.TenantSeen);
    }

    [Fact]
    public async Task FirstTick_AnchorsAndRunsNothing()
    {
        // Deploying at 06:45 must not immediately fire the 03:00 batch. A job
        // that has never run has no missed occurrence to catch up on, because
        // there was nothing to miss.
        var handler = new RecordingHandler(_tenants);
        JobScheduler scheduler = CreateScheduler(handler, cron: "0 3 * * *");

        _clock.UtcNow = new DateTimeOffset(2026, 8, 5, 6, 45, 0, TimeSpan.Zero);

        Assert.Equal(0, (await scheduler.TickAsync(TenantA, default)).Value.Ran);
        Assert.Equal(0, handler.Runs);
    }

    [Fact]
    public async Task Tick_ForATenantThatNoLongerExists_IsRefused()
    {
        // A scheduler that skipped this keeps processing a customer through
        // their offboarding.
        var handler = new RecordingHandler(_tenants);
        JobScheduler scheduler = CreateScheduler(handler);

        Assert.True((await scheduler.TickAsync(Unknown, default)).IsFailure);
        Assert.Equal(0, handler.Runs);
    }

    [Fact]
    public async Task Tick_DoesNotRunAJobThatIsNotDueYet()
    {
        var handler = new RecordingHandler(_tenants);
        JobScheduler scheduler = CreateScheduler(handler, cron: "0 3 * * *");

        _clock.UtcNow = new DateTimeOffset(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal(0, (await scheduler.TickAsync(TenantA, default)).Value.Ran);
        Assert.Equal(0, handler.Runs);
    }

    [Fact]
    public async Task Tick_DoesNotRunTheSameOccurrenceTwice()
    {
        var handler = new RecordingHandler(_tenants);
        JobScheduler scheduler = CreateScheduler(handler);

        await scheduler.TickAsync(TenantA, default);
        _clock.Advance(TimeSpan.FromMinutes(6));
        await scheduler.TickAsync(TenantA, default);
        await scheduler.TickAsync(TenantA, default);

        Assert.Equal(1, handler.Runs);
    }

    // ── overlap ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Tick_DoesNotStartASecondCopyWhileOneIsRunning()
    {
        // A nightly reconciliation that takes longer than a day starts a
        // second copy, and the two race over the same rows.
        var handler = new ReentrantHandler();
        JobScheduler scheduler = CreateScheduler(handler);
        handler.Scheduler = scheduler;
        handler.TenantId = TenantA;

        await scheduler.TickAsync(TenantA, default);
        _clock.Advance(TimeSpan.FromMinutes(6));
        await scheduler.TickAsync(TenantA, default);

        Assert.Equal(1, handler.Runs);
        Assert.Equal(1, handler.OverlapsObserved);
    }

    [Fact]
    public async Task Tick_ReleasesTheLockAfterTheTimeout()
    {
        // Without this a crashed run blocks the job forever, and the failure
        // is silent.
        var handler = new RecordingHandler(_tenants);
        JobScheduler scheduler = CreateScheduler(handler, timeout: TimeSpan.FromMinutes(10));

        await scheduler.TickAsync(TenantA, default);
        _clock.Advance(TimeSpan.FromMinutes(6));

        JobState state = _state.GetOrCreate("nightly", TenantA);
        state.RunningSinceUtc = _clock.UtcNow;

        Assert.Equal(1, (await scheduler.TickAsync(TenantA, default)).Value.SkippedOverlapping);

        _clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(1, (await scheduler.TickAsync(TenantA, default)).Value.Ran);
    }

    // ── catch-up ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RunOnce_AfterAnOutage_RunsOnceNotOncePerMissedOccurrence()
    {
        // A host down for three days has missed three nightly runs. Replaying
        // them texts every patient three times.
        var handler = new RecordingHandler(_tenants);
        JobScheduler scheduler = CreateScheduler(handler, cron: "0 3 * * *");

        _clock.UtcNow = new DateTimeOffset(2026, 8, 1, 2, 59, 0, TimeSpan.Zero);
        await scheduler.TickAsync(TenantA, default);
        _clock.UtcNow = new DateTimeOffset(2026, 8, 1, 3, 0, 0, TimeSpan.Zero);
        await scheduler.TickAsync(TenantA, default);
        Assert.Equal(1, handler.Runs);

        // Three days later, one tick.
        _clock.UtcNow = new DateTimeOffset(2026, 8, 4, 9, 0, 0, TimeSpan.Zero);
        await scheduler.TickAsync(TenantA, default);

        Assert.Equal(2, handler.Runs);
    }

    [Fact]
    public async Task Skip_AfterAnOutage_WaitsForTheNextScheduledOccurrence()
    {
        // A 07:00 digest is worthless at 14:00 and should not be sent.
        var handler = new RecordingHandler(_tenants);
        JobScheduler scheduler = CreateScheduler(handler, CatchUpPolicy.Skip, "0 7 * * *");

        _clock.UtcNow = new DateTimeOffset(2026, 8, 1, 6, 59, 0, TimeSpan.Zero);
        await scheduler.TickAsync(TenantA, default);
        _clock.UtcNow = new DateTimeOffset(2026, 8, 1, 7, 0, 0, TimeSpan.Zero);
        await scheduler.TickAsync(TenantA, default);
        Assert.Equal(1, handler.Runs);

        _clock.UtcNow = new DateTimeOffset(2026, 8, 4, 14, 0, 0, TimeSpan.Zero);
        await scheduler.TickAsync(TenantA, default);

        Assert.Equal(1, handler.Runs);
    }

    // ── failure handling ──────────────────────────────────────────────────

    [Fact]
    public async Task TransientFailure_IsRetriedOnTheNextTick()
    {
        var handler = new FailingHandler(ErrorCategory.Transient);
        JobScheduler scheduler = CreateScheduler(handler);

        await scheduler.TickAsync(TenantA, default);
        _clock.Advance(TimeSpan.FromMinutes(6));
        await scheduler.TickAsync(TenantA, default);
        _clock.Advance(TimeSpan.FromMinutes(6));
        await scheduler.TickAsync(TenantA, default);

        Assert.Equal(2, handler.Runs);
    }

    [Fact]
    public async Task NonTransientFailure_AdvancesPastTheOccurrence()
    {
        // Same rule as the delivery scheduler, derived from the taxonomy. A
        // validation failure will fail identically next time, and wedging on
        // it stops every later occurrence too.
        var handler = new FailingHandler(ErrorCategory.Validation);
        JobScheduler scheduler = CreateScheduler(handler, cron: "0 3 * * *");

        _clock.UtcNow = new DateTimeOffset(2026, 8, 1, 2, 59, 0, TimeSpan.Zero);
        await scheduler.TickAsync(TenantA, default);
        _clock.UtcNow = new DateTimeOffset(2026, 8, 1, 3, 0, 0, TimeSpan.Zero);
        await scheduler.TickAsync(TenantA, default);
        await scheduler.TickAsync(TenantA, default);

        Assert.Equal(1, handler.Runs);
    }

    [Fact]
    public async Task RepeatedTransientFailure_StopsAtTheAttemptLimit()
    {
        // A daily schedule, so only ONE occurrence falls in the window. After
        // three attempts the scheduler gives up on it — and moves on rather
        // than wedging, which is why a schedule with more occurrences would
        // legitimately show more runs.
        var handler = new FailingHandler(ErrorCategory.Transient);
        JobScheduler scheduler = CreateScheduler(handler, cron: "0 3 * * *");

        _clock.UtcNow = new DateTimeOffset(2026, 8, 1, 2, 59, 0, TimeSpan.Zero);
        await scheduler.TickAsync(TenantA, default);

        _clock.UtcNow = new DateTimeOffset(2026, 8, 1, 3, 0, 0, TimeSpan.Zero);

        for (int i = 0; i < 5; i++)
        {
            await scheduler.TickAsync(TenantA, default);
        }

        Assert.Equal(3, handler.Runs);
    }

    [Fact]
    public async Task Context_CarriesTheScheduledTimeNotTheStartTime()
    {
        // A nightly job that starts late must still process "yesterday". A
        // handler reading the wall clock silently skips a day when the host
        // was slow.
        var handler = new RecordingHandler(_tenants);
        JobScheduler scheduler = CreateScheduler(handler, cron: "0 3 * * *");

        _clock.UtcNow = new DateTimeOffset(2026, 8, 5, 2, 0, 0, TimeSpan.Zero);
        await scheduler.TickAsync(TenantA, default);

        // Host was slow: the tick lands at 06:45 but the occurrence is 03:00.
        _clock.UtcNow = new DateTimeOffset(2026, 8, 5, 6, 45, 0, TimeSpan.Zero);
        await scheduler.TickAsync(TenantA, default);

        Assert.Equal(
            new DateTimeOffset(2026, 8, 5, 3, 0, 0, TimeSpan.Zero),
            handler.ScheduledFor);
    }

    // ── handlers ──────────────────────────────────────────────────────────

    private sealed class RecordingHandler(ITenantContextAccessor accessor) : IJobHandler
    {
        public string HandlerName => "recording";

        public int Runs { get; private set; }

        public Guid? TenantSeen { get; private set; }

        public DateTimeOffset ScheduledFor { get; private set; }

        public Task<Result> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Runs++;
            TenantSeen = accessor.Current?.TenantId;
            ScheduledFor = context.ScheduledForUtc;

            return Task.FromResult(Result.Success());
        }
    }

    private sealed class FailingHandler(ErrorCategory category) : IJobHandler
    {
        public string HandlerName => "failing";

        public int Runs { get; private set; }

        public Task<Result> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Runs++;

            return Task.FromResult(Result.Failure(new Error(
                ErrorCodes.TransientFailure, "it failed", category)));
        }
    }

    /// <summary>Ticks the scheduler from inside a run, to observe overlap.</summary>
    private sealed class ReentrantHandler : IJobHandler
    {
        public string HandlerName => "reentrant";

        public JobScheduler? Scheduler { get; set; }

        public Guid TenantId { get; set; }

        public int Runs { get; private set; }

        public int OverlapsObserved { get; private set; }

        public async Task<Result> ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Runs++;

            TickOutcome inner = (await Scheduler!.TickAsync(TenantId, cancellationToken)).Value;
            OverlapsObserved += inner.SkippedOverlapping;

            return Result.Success();
        }
    }

    private sealed class SingleTenantStore(Guid known) : ITenantStore
    {
        public Task<Result<TenantDescriptor>> GetAsync(Guid tenantId, CancellationToken cancellationToken)
            => Task.FromResult(tenantId == known
                ? Result<TenantDescriptor>.FromValue(new TenantDescriptor(
                    known, "demo", "eu-west", TenantIsolationMode.SharedSchema, Guid.NewGuid()))
                : Result.Failure<TenantDescriptor>(new Error(
                    ErrorCodes.TenantScopeViolation,
                    "The requested resource was not found.",
                    ErrorCategory.NotFound)));
    }
}
