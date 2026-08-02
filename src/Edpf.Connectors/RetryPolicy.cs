using System;
using Edpf.Core.Guards;

namespace Edpf.Connectors;

/// <summary>What a connector should do about a failed request (Phase 26f).</summary>
public enum RetryDecision
{
    /// <summary>Wait and try again.</summary>
    Retry = 0,

    /// <summary>Stop. The attempt budget is spent.</summary>
    GiveUp = 1,

    /// <summary>
    /// Stop immediately. Retrying cannot help and would only add load.
    /// </summary>
    Fatal = 2,
}

/// <summary>The decision and, when retrying, how long to wait (Phase 26f).</summary>
public sealed class RetryVerdict
{
    /// <summary>Initializes a verdict.</summary>
    /// <param name="decision">What to do.</param>
    /// <param name="delay">How long to wait before retrying.</param>
    /// <param name="reason">Why, for the connector run's audit record.</param>
    public RetryVerdict(RetryDecision decision, TimeSpan delay, string reason)
    {
        Decision = decision;
        Delay = delay;
        Reason = Guard.NotNullOrWhiteSpace(reason, nameof(reason));
    }

    /// <summary>What to do.</summary>
    public RetryDecision Decision { get; }

    /// <summary>How long to wait before retrying.</summary>
    public TimeSpan Delay { get; }

    /// <summary>Why.</summary>
    public string Reason { get; }
}

/// <summary>
/// Decides whether and when to retry a connector request (Phase 26f).
/// </summary>
/// <remarks>
/// <para>
/// Three rules, each closing a way of turning a transient problem into an
/// outage.
/// </para>
/// <para>
/// **A source's <c>Retry-After</c> wins over our backoff, always.** It is the
/// only party that knows when its own limit resets. Ignoring it in favour of
/// a locally computed delay is how a rate-limited connector converts a
/// throttle into a ban.
/// </para>
/// <para>
/// **Backoff carries jitter.** Without it, every connector in a fleet that hit
/// the same outage retries at the same instant, and the recovering source is
/// hit by a synchronised thundering herd — often the thing that keeps it
/// down. The jitter here is derived deterministically from the attempt and a
/// per-connector seed rather than drawn from a random source, so a connector's
/// backoff schedule is reproducible in a test and in an incident review.
/// </para>
/// <para>
/// **Some failures are fatal.** Retrying a 401 or a 403 will not fix
/// credentials, and retrying a 400 will not fix a malformed request. Both add
/// load to a source that has already said no, and a retried 401 is how an
/// account gets locked.
/// </para>
/// </remarks>
public sealed class RetryPolicy
{
    private readonly int _seed;

    /// <summary>Initializes a policy.</summary>
    /// <param name="maximumAttempts">Total attempts including the first.</param>
    /// <param name="baseDelay">The first retry's delay before jitter.</param>
    /// <param name="maximumDelay">The ceiling on any single delay.</param>
    /// <param name="jitterSeed">
    /// A per-connector seed. Two connectors with different seeds retry at
    /// different moments; the same connector retries the same way every run.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">A bound is not positive, or the ceiling is below the base.</exception>
    public RetryPolicy(
        int maximumAttempts = 5,
        TimeSpan? baseDelay = null,
        TimeSpan? maximumDelay = null,
        int jitterSeed = 0)
    {
        MaximumAttempts = Guard.Positive(maximumAttempts, nameof(maximumAttempts));
        BaseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
        MaximumDelay = maximumDelay ?? TimeSpan.FromMinutes(2);
        _seed = jitterSeed;

        if (BaseDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(baseDelay), BaseDelay, "The base delay must be positive.");
        }

        if (MaximumDelay < BaseDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDelay),
                MaximumDelay,
                "The maximum delay must be at least the base delay; otherwise the ceiling silently "
                + "cancels the backoff.");
        }
    }

    /// <summary>Total attempts including the first.</summary>
    public int MaximumAttempts { get; }

    /// <summary>The first retry's delay before jitter.</summary>
    public TimeSpan BaseDelay { get; }

    /// <summary>The ceiling on any single delay.</summary>
    public TimeSpan MaximumDelay { get; }

    /// <summary>
    /// Decides what to do after a failed attempt.
    /// </summary>
    /// <param name="attempt">Which attempt just failed, counting from 1.</param>
    /// <param name="outcome">How it failed.</param>
    /// <param name="retryAfter">The source's own <c>Retry-After</c>, when it supplied one.</param>
    /// <returns>The verdict.</returns>
    public RetryVerdict Decide(int attempt, RequestOutcome outcome, TimeSpan? retryAfter = null)
    {
        if (outcome == RequestOutcome.Success)
        {
            return new RetryVerdict(RetryDecision.GiveUp, TimeSpan.Zero, "The request succeeded.");
        }

        if (outcome is RequestOutcome.Unauthorized or RequestOutcome.Forbidden or RequestOutcome.BadRequest)
        {
            // Retrying adds load to a source that has already said no, and a
            // retried 401 is how an account gets locked.
            return new RetryVerdict(
                RetryDecision.Fatal,
                TimeSpan.Zero,
                $"A {outcome} response will not become a success by being repeated.");
        }

        if (attempt >= MaximumAttempts)
        {
            return new RetryVerdict(
                RetryDecision.GiveUp,
                TimeSpan.Zero,
                $"All {MaximumAttempts} attempts are spent.");
        }

        if (retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero)
        {
            // The source is the only party that knows when its own limit
            // resets. Honoured even when it exceeds our own ceiling — ignoring
            // it is how a throttle becomes a ban.
            return new RetryVerdict(
                RetryDecision.Retry,
                retryAfter.Value,
                "Honouring the source's Retry-After, which overrides the computed backoff.");
        }

        return new RetryVerdict(
            RetryDecision.Retry,
            DelayFor(attempt),
            $"Exponential backoff with jitter after attempt {attempt}.");
    }

    /// <summary>
    /// The backoff delay for an attempt, jitter included.
    /// </summary>
    /// <param name="attempt">Which attempt just failed, counting from 1.</param>
    /// <returns>The delay, never above <see cref="MaximumDelay"/>.</returns>
    public TimeSpan DelayFor(int attempt)
    {
        if (attempt < 1)
        {
            return BaseDelay;
        }

        // Doubling, capped before the multiplication can overflow. An attempt
        // count of 60 would otherwise shift past the range of a long and wrap
        // to a negative delay.
        int exponent = Math.Min(attempt - 1, 20);
        double multiplier = Math.Pow(2, exponent);

        double ticks = Math.Min(BaseDelay.Ticks * multiplier, MaximumDelay.Ticks);

        // Deterministic jitter in [0.5, 1.0] of the computed delay, derived
        // from the attempt and the connector's seed. Reproducible in a test
        // and in an incident review, unlike a draw from a random source, while
        // still spreading a fleet's retries apart.
        uint hash = Mix((uint)(_seed * 397) ^ (uint)attempt);
        double jitter = 0.5 + ((hash % 1000) / 1000.0 * 0.5);

        return TimeSpan.FromTicks((long)(ticks * jitter));
    }

    private static uint Mix(uint value)
    {
        // A small avalanche so adjacent attempts do not produce adjacent
        // jitter, which would leave a fleet's retries clustered anyway.
        unchecked
        {
            value ^= value >> 16;
            value *= 0x7FEB352D;
            value ^= value >> 15;
            value *= 0x846CA68B;
            value ^= value >> 16;
            return value;
        }
    }
}

/// <summary>How a connector request ended (Phase 26f).</summary>
public enum RequestOutcome
{
    /// <summary>The request succeeded.</summary>
    Success = 0,

    /// <summary>A transient network or server failure.</summary>
    Transient = 1,

    /// <summary>The source applied a rate limit.</summary>
    RateLimited = 2,

    /// <summary>The request timed out.</summary>
    Timeout = 3,

    /// <summary>Credentials were rejected.</summary>
    Unauthorized = 4,

    /// <summary>The credentials were accepted but the operation is not permitted.</summary>
    Forbidden = 5,

    /// <summary>The request was malformed.</summary>
    BadRequest = 6,
}
