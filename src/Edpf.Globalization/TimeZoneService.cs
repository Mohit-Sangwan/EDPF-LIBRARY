using System;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Globalization;

/// <summary>
/// An instant plus the IANA zone in which it was meaningful (Phase 27
/// §"Time policy").
/// </summary>
/// <remarks>
/// <para>
/// The policy, stated once: **store UTC, carry the originating IANA zone
/// alongside where local wall-clock time is semantically meaningful, convert
/// only at the presentation edge.**
/// </para>
/// <para>
/// The zone is not decoration. "The medication was given at 08:00" is a
/// clinical fact about local time; if the ward moves across a DST boundary,
/// or the record is read from another region, the UTC instant alone cannot
/// reconstruct what the nurse saw on the clock. Shift boundaries, appointment
/// times and administration times all carry this property.
/// </para>
/// </remarks>
public sealed class ZonedInstant
{
    /// <summary>
    /// Initializes a zoned instant.
    /// </summary>
    /// <param name="utc">The instant, in UTC.</param>
    /// <param name="ianaZoneId">The IANA zone, e.g. <c>Asia/Kolkata</c>.</param>
    /// <exception cref="ArgumentException">The instant is not UTC, or the zone is blank.</exception>
    public ZonedInstant(DateTimeOffset utc, string ianaZoneId)
    {
        if (utc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Instants are stored in UTC; the local view is derived from the zone, not baked into the offset.",
                nameof(utc));
        }

        Utc = utc;
        IanaZoneId = Guard.NotNullOrWhiteSpace(ianaZoneId, nameof(ianaZoneId));
    }

    /// <summary>The instant, in UTC.</summary>
    public DateTimeOffset Utc { get; }

    /// <summary>The IANA zone in which the local time was meaningful.</summary>
    public string IanaZoneId { get; }
}

/// <summary>
/// Timezone conversion honouring **historical** rules (Phase 27 §"Time
/// policy").
/// </summary>
/// <remarks>
/// A date in 2015 must convert using 2015's rules, not today's. Zones change:
/// Russia abolished DST in 2011 and changed again in 2014; Turkey moved to
/// permanent UTC+3 in 2016; India has never observed DST but its offset is a
/// half-hour, which naive code frequently truncates. Converting a historical
/// clinical record with current rules silently shifts it, which is why
/// <see cref="TimeZoneInfo"/> is used rather than a stored fixed offset.
/// </remarks>
public sealed class TimeZoneService
{
    /// <summary>
    /// Converts a stored instant to its local representation.
    /// </summary>
    /// <param name="instant">The stored instant and zone.</param>
    /// <returns>
    /// The local time, or failure when the zone is unknown to the platform —
    /// refused rather than silently falling back to UTC, which would shift
    /// every displayed time.
    /// </returns>
    public static Result<DateTimeOffset> ToLocal(ZonedInstant instant)
    {
        Guard.NotNull(instant, nameof(instant));

        Result<TimeZoneInfo> zone = FindZone(instant.IanaZoneId);
        if (zone.IsFailure)
        {
            return Result.Failure<DateTimeOffset>(zone.Error!);
        }

        // TimeZoneInfo applies the rule that was in force at that instant,
        // which is the whole point.
        return Result.Success(TimeZoneInfo.ConvertTime(instant.Utc, zone.Value));
    }

    /// <summary>
    /// Classifies a local wall-clock time against DST transitions.
    /// </summary>
    /// <param name="localTime">The wall-clock time as written.</param>
    /// <param name="ianaZoneId">The zone it was written in.</param>
    /// <returns>Whether the time is unambiguous, skipped, or repeated.</returns>
    /// <remarks>
    /// A job scheduled at 02:30 that runs twice or not at all across a DST
    /// transition is a real and recurring production defect (Phase 25). The
    /// caller must decide what to do; this reports which case it is instead
    /// of silently picking one.
    /// </remarks>
    public static Result<LocalTimeKind> ClassifyLocalTime(DateTime localTime, string ianaZoneId)
    {
        Result<TimeZoneInfo> zone = FindZone(ianaZoneId);
        if (zone.IsFailure)
        {
            return Result.Failure<LocalTimeKind>(zone.Error!);
        }

        DateTime unspecified = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);

        if (zone.Value.IsInvalidTime(unspecified))
        {
            // The clock jumped forward over this time; it never occurred.
            return Result.Success(LocalTimeKind.Skipped);
        }

        if (zone.Value.IsAmbiguousTime(unspecified))
        {
            // The clock fell back; this wall-clock time occurred twice.
            return Result.Success(LocalTimeKind.Repeated);
        }

        return Result.Success(LocalTimeKind.Unambiguous);
    }

    private static Result<TimeZoneInfo> FindZone(string ianaZoneId)
    {
        Guard.NotNullOrWhiteSpace(ianaZoneId, nameof(ianaZoneId));

        try
        {
            return Result.Success(TimeZoneInfo.FindSystemTimeZoneById(ianaZoneId));
        }
        catch (TimeZoneNotFoundException)
        {
            return Result.Failure<TimeZoneInfo>(new Error(
                ErrorCodes.ConfigurationInvalid,
                $"Time zone '{ianaZoneId}' is not available on this platform.",
                ErrorCategory.Configuration));
        }
        catch (InvalidTimeZoneException)
        {
            return Result.Failure<TimeZoneInfo>(new Error(
                ErrorCodes.ConfigurationInvalid,
                $"Time zone '{ianaZoneId}' has corrupt rule data on this platform.",
                ErrorCategory.Configuration));
        }
    }
}

/// <summary>How a wall-clock time relates to a DST transition.</summary>
public enum LocalTimeKind
{
    /// <summary>The time occurred exactly once.</summary>
    Unambiguous = 0,

    /// <summary>
    /// The clock jumped forward over this time; it never occurred. A job
    /// scheduled here would silently never run.
    /// </summary>
    Skipped = 1,

    /// <summary>
    /// The clock fell back; this wall-clock time occurred twice. A job
    /// scheduled here would silently run twice.
    /// </summary>
    Repeated = 2,
}
