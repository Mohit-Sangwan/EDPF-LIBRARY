using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Tenancy;
using Edpf.Core.Guards;
using Edpf.Core.Time;

namespace Edpf.Jobs;

/// <summary>What to do about occurrences missed while the host was down.</summary>
public enum CatchUpPolicy
{
    /// <summary>
    /// Run once, however many occurrences were missed.
    /// </summary>
    /// <remarks>
    /// **The right default, and not the obvious one.** A host down for three
    /// days has missed three nightly reminder runs; replaying them sends every
    /// patient three texts. For almost every scheduled job the work is
    /// idempotent-by-intent — "make the world match the schedule" — and doing
    /// it once achieves that.
    /// </remarks>
    RunOnce = 0,

    /// <summary>
    /// Skip everything missed and wait for the next scheduled occurrence.
    /// </summary>
    /// <remarks>
    /// For jobs whose value is entirely in their timing: a "good morning"
    /// digest at 07:00 is worthless at 14:00 and should simply not be sent.
    /// **"Missed" needs a tolerance to mean anything** — a tick thirty seconds
    /// late has not missed anything — so this policy is governed by
    /// <see cref="JobDefinition.MaxDelay"/>.
    /// </remarks>
    Skip = 1,
}

/// <summary>A registered scheduled job.</summary>
public sealed class JobDefinition
{
    /// <summary>
    /// Declares a job.
    /// </summary>
    /// <param name="jobId">The job's stable id.</param>
    /// <param name="schedule">When it runs.</param>
    /// <param name="handlerName">Which handler executes it.</param>
    /// <param name="catchUp">What to do about missed occurrences.</param>
    /// <param name="maxAttempts">How many times a failing run is retried.</param>
    /// <param name="timeout">
    /// How long a run may take before it is considered abandoned and its
    /// overlap lock released.
    /// </param>
    /// <param name="maxDelay">
    /// How late an occurrence may still be run under
    /// <see cref="CatchUpPolicy.Skip"/>. Defaults to fifteen minutes, which is
    /// generous for a tick loop and short enough that a 07:00 digest is not
    /// sent at 14:00.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="schedule"/> is null.</exception>
    /// <exception cref="ArgumentException">An id is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The attempt count or timeout is not positive.</exception>
    public JobDefinition(
        string jobId,
        CronSchedule schedule,
        string handlerName,
        CatchUpPolicy catchUp = CatchUpPolicy.RunOnce,
        int maxAttempts = 3,
        TimeSpan? timeout = null,
        TimeSpan? maxDelay = null)
    {
        JobId = Guard.NotNullOrWhiteSpace(jobId, nameof(jobId));
        Schedule = Guard.NotNull(schedule, nameof(schedule));
        HandlerName = Guard.NotNullOrWhiteSpace(handlerName, nameof(handlerName));
        CatchUp = catchUp;

        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        Timeout = timeout ?? TimeSpan.FromHours(1);

        if (Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        MaxAttempts = maxAttempts;
        MaxDelay = maxDelay ?? TimeSpan.FromMinutes(15);
    }

    /// <summary>The job's stable id.</summary>
    public string JobId { get; }

    /// <summary>When it runs.</summary>
    public CronSchedule Schedule { get; }

    /// <summary>Which handler executes it.</summary>
    public string HandlerName { get; }

    /// <summary>What to do about missed occurrences.</summary>
    public CatchUpPolicy CatchUp { get; }

    /// <summary>How many times a failing run is retried.</summary>
    public int MaxAttempts { get; }

    /// <summary>How long a run may take before it is considered abandoned.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>How late an occurrence may still run under <see cref="CatchUpPolicy.Skip"/>.</summary>
    public TimeSpan MaxDelay { get; }
}

/// <summary>What a job knows about the run it is in.</summary>
public sealed class JobContext
{
    /// <summary>
    /// Describes a run.
    /// </summary>
    /// <param name="jobId">Which job.</param>
    /// <param name="tenantId">Which tenant it runs for.</param>
    /// <param name="scheduledForUtc">The occurrence this run is servicing.</param>
    /// <param name="attemptNumber">Which attempt, from 1.</param>
    public JobContext(string jobId, Guid tenantId, DateTimeOffset scheduledForUtc, int attemptNumber)
    {
        JobId = jobId;
        TenantId = tenantId;
        ScheduledForUtc = scheduledForUtc;
        AttemptNumber = attemptNumber;
    }

    /// <summary>Which job.</summary>
    public string JobId { get; }

    /// <summary>Which tenant it runs for.</summary>
    public Guid TenantId { get; }

    /// <summary>
    /// The occurrence this run is servicing.
    /// </summary>
    /// <remarks>
    /// The scheduled time, not the actual start. A nightly job that starts
    /// late must still process "yesterday", and a handler that reads the wall
    /// clock instead silently skips a day when the host was slow.
    /// </remarks>
    public DateTimeOffset ScheduledForUtc { get; }

    /// <summary>Which attempt, from 1.</summary>
    public int AttemptNumber { get; }
}

/// <summary>Executes one kind of job.</summary>
public interface IJobHandler
{
    /// <summary>The name definitions reference.</summary>
    string HandlerName { get; }

    /// <summary>
    /// Does the work.
    /// </summary>
    /// <param name="context">What run this is.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>
    /// Success, or a failure. A failure carrying
    /// <see cref="ErrorCategory.Transient"/> is retried; anything else is not,
    /// on the same rule the delivery scheduler uses.
    /// </returns>
    Task<Result> ExecuteAsync(JobContext context, CancellationToken cancellationToken);
}

/// <summary>The state a scheduler keeps about one job on one tenant.</summary>
public sealed class JobState
{
    /// <summary>Which job.</summary>
    public string JobId { get; init; } = string.Empty;

    /// <summary>Which tenant.</summary>
    public Guid TenantId { get; init; }

    /// <summary>The last occurrence that completed, or null.</summary>
    public DateTimeOffset? LastCompletedOccurrenceUtc { get; set; }

    /// <summary>When the currently-running attempt started, or null when idle.</summary>
    public DateTimeOffset? RunningSinceUtc { get; set; }

    /// <summary>How many consecutive attempts have failed.</summary>
    public int ConsecutiveFailures { get; set; }
}

/// <summary>Where job state lives between runs.</summary>
/// <remarks>
/// A seam because the state must be shared across every node running the
/// scheduler. An in-memory implementation prevents overlap within one process
/// and not between two, which is exactly the case that matters once a
/// deployment scales out.
/// </remarks>
public interface IJobStateStore
{
    /// <summary>Reads state, creating it if absent.</summary>
    /// <param name="jobId">Which job.</param>
    /// <param name="tenantId">Which tenant.</param>
    /// <returns>The state.</returns>
    JobState GetOrCreate(string jobId, Guid tenantId);
}

/// <summary>Holds job state in process memory. Single node only.</summary>
/// <remarks>
/// **Prevents overlap within one process and not between two.** Stated
/// plainly, because a deployment that scales to two nodes with this store gets
/// two concurrent nightly reconciliations and no warning.
/// </remarks>
public sealed class InMemoryJobStateStore : IJobStateStore
{
    private readonly Dictionary<string, JobState> _states = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public JobState GetOrCreate(string jobId, Guid tenantId)
    {
        Guard.NotNullOrWhiteSpace(jobId, nameof(jobId));

        string key = tenantId.ToString("D") + "|" + jobId;

        if (!_states.TryGetValue(key, out JobState? state))
        {
            state = new JobState { JobId = jobId, TenantId = tenantId };
            _states[key] = state;
        }

        return state;
    }
}

/// <summary>
/// Runs scheduled jobs, per tenant, without overlapping them.
/// </summary>
/// <remarks>
/// <para>
/// Driven by a caller — a worker's timer, a container's cron sidecar — rather
/// than by an internal timer, so a tick is testable without waiting and a
/// paused host cannot appear healthy while running nothing.
/// </para>
/// <para>
/// **Overlap prevention is the control that earns this class.** A nightly
/// reconciliation that takes longer than a day starts a second copy, and the
/// two race over the same rows. The lock is held in shared state and released
/// on completion or timeout, so a crashed run does not block the job forever.
/// </para>
/// </remarks>
public sealed class JobScheduler
{
    private readonly Dictionary<string, JobDefinition> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IJobHandler> _handlers = new(StringComparer.Ordinal);
    private readonly IJobStateStore _state;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly ITenantStore _tenants;
    private readonly IClock _clock;

    /// <summary>
    /// Composes the scheduler.
    /// </summary>
    /// <param name="definitions">The jobs this deployment runs.</param>
    /// <param name="handlers">The handlers they name.</param>
    /// <param name="state">Where job state lives.</param>
    /// <param name="tenantAccessor">Ambient tenant, pushed for each run.</param>
    /// <param name="tenants">Resolves a tenant before running for it.</param>
    /// <param name="clock">Time source (Z.3 rule 4).</param>
    /// <exception cref="ArgumentNullException">Any dependency is null.</exception>
    /// <exception cref="ArgumentException">
    /// A definition names a handler that was not supplied. A composition-time
    /// failure, because the alternative is a job that silently never runs and
    /// is noticed a quarter later (ADR-014).
    /// </exception>
    public JobScheduler(
        IReadOnlyList<JobDefinition> definitions,
        IReadOnlyList<IJobHandler> handlers,
        IJobStateStore state,
        ITenantContextAccessor tenantAccessor,
        ITenantStore tenants,
        IClock clock)
    {
        Guard.NotNull(definitions, nameof(definitions));
        Guard.NotNull(handlers, nameof(handlers));

        _state = Guard.NotNull(state, nameof(state));
        _tenantAccessor = Guard.NotNull(tenantAccessor, nameof(tenantAccessor));
        _tenants = Guard.NotNull(tenants, nameof(tenants));
        _clock = Guard.NotNull(clock, nameof(clock));

        foreach (IJobHandler handler in handlers)
        {
            _handlers[handler.HandlerName] = handler;
        }

        foreach (JobDefinition definition in definitions)
        {
            if (!_handlers.ContainsKey(definition.HandlerName))
            {
                throw new ArgumentException(
                    "A job names a handler that is not registered. A job with no handler never runs, and "
                    + "nothing else would report that.",
                    nameof(handlers));
            }

            _jobs[definition.JobId] = definition;
        }
    }

    /// <summary>
    /// Runs every job whose occurrence has passed, for one tenant.
    /// </summary>
    /// <param name="tenantId">Which tenant to run for.</param>
    /// <param name="cancellationToken">Cancels the tick.</param>
    /// <returns>What the tick did.</returns>
    public async Task<Result<TickOutcome>> TickAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        Result<TenantDescriptor> tenant = await _tenants
            .GetAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        // A job cannot run for a tenant that no longer exists. A scheduler
        // that skipped this would keep processing a customer through their
        // offboarding.
        if (tenant.IsFailure)
        {
            return Result.Failure<TickOutcome>(tenant.Error!);
        }

        DateTimeOffset now = _clock.UtcNow;
        int ran = 0;
        int skippedOverlapping = 0;
        int notDue = 0;

        foreach (JobDefinition job in _jobs.Values)
        {
            JobState state = _state.GetOrCreate(job.JobId, tenantId);

            if (state.RunningSinceUtc is not null)
            {
                if (now - state.RunningSinceUtc.Value < job.Timeout)
                {
                    // Still running. A second copy would race the first over
                    // the same rows.
                    skippedOverlapping++;
                    continue;
                }

                // Past its timeout, so the run is presumed dead and the lock is
                // released. Without this a crashed run blocks the job forever
                // and the failure is silent.
                state.RunningSinceUtc = null;
            }

            DateTimeOffset? due = NextDue(job, state, now);

            if (due is null || due > now)
            {
                notDue++;
                continue;
            }

            state.RunningSinceUtc = now;

            try
            {
                Result executed = await RunAsync(job, tenant.Value, due.Value, state, cancellationToken)
                    .ConfigureAwait(false);

                if (executed.IsSuccess)
                {
                    ran++;
                }
            }
            finally
            {
                state.RunningSinceUtc = null;
            }
        }

        return new TickOutcome(ran, skippedOverlapping, notDue);
    }

    /// <summary>
    /// The occurrence this job owes, honouring its catch-up policy.
    /// </summary>
    /// <remarks>
    /// Mutates <paramref name="state"/> on the first encounter to anchor the
    /// job, which is why this is not a pure function.
    /// </remarks>
    private static DateTimeOffset? NextDue(JobDefinition job, JobState state, DateTimeOffset now)
    {
        if (state.LastCompletedOccurrenceUtc is null)
        {
            // The first tick ANCHORS the job at the present and runs nothing.
            //
            // The alternative — treat the most recent past occurrence as owed —
            // means deploying at 06:45 immediately fires the 03:00 batch,
            // which for a nightly reminder run is a surprise burst of texts
            // caused by a release. A job that has never run has no missed
            // occurrence to catch up on, because there was nothing to miss.
            state.LastCompletedOccurrenceUtc = now;
            return null;
        }

        DateTimeOffset? next = job.Schedule.NextOccurrence(state.LastCompletedOccurrenceUtc.Value);

        if (next is null || next > now)
        {
            return next;
        }

        if (job.CatchUp == CatchUpPolicy.Skip)
        {
            // Walk to the MOST RECENT occurrence that has passed, not to the
            // first future one. Skipping to the future would discard the
            // occurrence that is due right now, which is not a missed
            // occurrence at all — an earlier version of this did exactly that
            // and a job with Skip never ran.
            DateTimeOffset mostRecent = next.Value;

            while (true)
            {
                DateTimeOffset? following = job.Schedule.NextOccurrence(mostRecent);

                if (following is null || following > now)
                {
                    break;
                }

                mostRecent = following.Value;
            }

            // Run it only if it is still fresh. "Missed" needs a tolerance to
            // mean anything: a tick thirty seconds late has missed nothing.
            if (now - mostRecent <= job.MaxDelay)
            {
                return mostRecent;
            }

            state.LastCompletedOccurrenceUtc = mostRecent;
            return job.Schedule.NextOccurrence(mostRecent);
        }

        // RunOnce: return the OLDEST missed occurrence and let the next tick
        // handle the following one if the job is genuinely behind. Replaying
        // three days of nightly reminders would text every patient three times.
        return next;
    }

    private async Task<Result> RunAsync(
        JobDefinition job,
        TenantDescriptor tenant,
        DateTimeOffset occurrence,
        JobState state,
        CancellationToken cancellationToken)
    {
        IJobHandler handler = _handlers[job.HandlerName];
        var context = new JobContext(
            job.JobId, tenant.TenantId, StorableInstant.Normalize(occurrence), state.ConsecutiveFailures + 1);

        Result executed;

        using (_tenantAccessor.Push(tenant))
        {
            executed = await handler.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        }

        if (executed.IsSuccess)
        {
            state.LastCompletedOccurrenceUtc = occurrence;
            state.ConsecutiveFailures = 0;
            return executed;
        }

        state.ConsecutiveFailures++;

        // The same rule as the delivery scheduler, derived from the taxonomy
        // rather than restated: transient is worth retrying, everything else
        // will fail the same way next time. A non-transient failure advances
        // past the occurrence so the job does not wedge on it forever.
        bool retryable = executed.Error!.Category == ErrorCategory.Transient;

        if (!retryable || state.ConsecutiveFailures >= job.MaxAttempts)
        {
            state.LastCompletedOccurrenceUtc = occurrence;
            state.ConsecutiveFailures = 0;
        }

        return executed;
    }
}

/// <summary>What one scheduler tick did.</summary>
public sealed class TickOutcome
{
    /// <summary>Records an outcome.</summary>
    /// <param name="ran">How many jobs ran successfully.</param>
    /// <param name="skippedOverlapping">How many were skipped because a run was already in progress.</param>
    /// <param name="notDue">How many were not due.</param>
    public TickOutcome(int ran, int skippedOverlapping, int notDue)
    {
        Ran = ran;
        SkippedOverlapping = skippedOverlapping;
        NotDue = notDue;
    }

    /// <summary>How many ran successfully.</summary>
    public int Ran { get; }

    /// <summary>
    /// How many were skipped because a run was already in progress.
    /// </summary>
    /// <remarks>
    /// Reported separately from <see cref="NotDue"/>, because a job that is
    /// permanently overlapping is a job that takes longer than its interval —
    /// an operational fact worth alerting on, and invisible if both collapse
    /// into "did not run".
    /// </remarks>
    public int SkippedOverlapping { get; }

    /// <summary>How many were not due.</summary>
    public int NotDue { get; }
}
