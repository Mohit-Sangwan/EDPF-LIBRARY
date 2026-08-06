using System;
using System.Collections.Generic;
using System.Globalization;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Jobs;

/// <summary>
/// A five-field cron expression: minute, hour, day of month, month, day of
/// week.
/// </summary>
/// <remarks>
/// <para>
/// **Occurrences are computed in UTC and nothing here takes a time zone**,
/// which is a decision rather than an omission. A job scheduled at 02:30 local
/// time runs **twice** on the day the clocks go back and **never** on the day
/// they go forward. For a nightly reconciliation that means either double
/// posting or a silent gap, and both are found weeks later by an accountant.
/// </para>
/// <para>
/// A deployment that genuinely needs local-time semantics — "the 09:00 clinic
/// list must be 09:00 whatever the season" — converts at the edge and accepts
/// the ambiguity explicitly. What it cannot do is get it by accident.
/// </para>
/// <para>
/// Supported syntax: <c>*</c>, a number, a list <c>1,15</c>, a range
/// <c>9-17</c>, and a step <c>*/15</c> or <c>0-30/10</c>. Deliberately not
/// supported: <c>@yearly</c>, <c>L</c>, <c>W</c>, <c>#</c> and seconds. Each is
/// a dialect difference between cron implementations, and a schedule that
/// means one thing on the developer's machine and another on the server is
/// worse than one that fails to parse.
/// </para>
/// </remarks>
public sealed class CronSchedule
{
    private const int SearchLimitMinutes = 366 * 24 * 60 * 4;

    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _daysOfMonth = new bool[32];
    private readonly bool[] _months = new bool[13];
    private readonly bool[] _daysOfWeek = new bool[7];
    private readonly bool _dayOfMonthRestricted;
    private readonly bool _dayOfWeekRestricted;

    private CronSchedule(string expression, string[] fields)
    {
        Expression = expression;

        Fill(_minutes, fields[0], 0, 59);
        Fill(_hours, fields[1], 0, 23);
        Fill(_daysOfMonth, fields[2], 1, 31);
        Fill(_months, fields[3], 1, 12);
        Fill(_daysOfWeek, fields[4], 0, 6);

        _dayOfMonthRestricted = fields[2] != "*";
        _dayOfWeekRestricted = fields[4] != "*";
    }

    /// <summary>The expression as written.</summary>
    public string Expression { get; }

    /// <summary>
    /// Parses an expression.
    /// </summary>
    /// <param name="expression">Five whitespace-separated fields.</param>
    /// <returns>The schedule, or a failure naming what was wrong.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is null.</exception>
    public static Result<CronSchedule> Parse(string expression)
    {
        Guard.NotNull(expression, nameof(expression));

        string[] fields = expression.Split(
            [' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

        if (fields.Length != 5)
        {
            return Result.Failure<CronSchedule>(new Error(
                ErrorCodes.ValidationFailed,
                "A cron expression has five fields: minute, hour, day of month, month, day of week.",
                ErrorCategory.Validation));
        }

        try
        {
            return new CronSchedule(expression, fields);
        }
        catch (FormatException)
        {
            return Result.Failure<CronSchedule>(new Error(
                ErrorCodes.ValidationFailed,
                "A field in the cron expression is not valid.",
                ErrorCategory.Validation));
        }
    }

    /// <summary>
    /// The first occurrence strictly after an instant.
    /// </summary>
    /// <param name="after">The instant to search from.</param>
    /// <returns>The next occurrence, or null when none falls within four years.</returns>
    /// <remarks>
    /// Strictly after, never at. An occurrence exactly at <paramref name="after"/>
    /// would make a scheduler that records "last run at T" and asks for "next
    /// after T" run the same minute forever.
    /// </remarks>
    public DateTimeOffset? NextOccurrence(DateTimeOffset after)
    {
        // Truncate to the minute and step forward. Minute-by-minute search is
        // unglamorous and it is correct for every field combination, including
        // the ones where day-of-month and day-of-week interact.
        DateTimeOffset candidate = new DateTimeOffset(
            after.UtcDateTime.Year,
            after.UtcDateTime.Month,
            after.UtcDateTime.Day,
            after.UtcDateTime.Hour,
            after.UtcDateTime.Minute,
            0,
            TimeSpan.Zero).AddMinutes(1);

        for (int i = 0; i < SearchLimitMinutes; i++)
        {
            if (Matches(candidate))
            {
                return candidate;
            }

            candidate = candidate.AddMinutes(1);
        }

        // "31 February" parses and never occurs. Null rather than an
        // exception: an impossible schedule is a configuration mistake to
        // report, not a crash at the first tick.
        return null;
    }

    /// <summary>True when an instant falls on this schedule.</summary>
    /// <param name="instant">The instant, considered in UTC.</param>
    public bool Matches(DateTimeOffset instant)
    {
        DateTime utc = instant.UtcDateTime;

        if (!_minutes[utc.Minute] || !_hours[utc.Hour] || !_months[utc.Month])
        {
            return false;
        }

        bool dayOfMonthMatches = _daysOfMonth[utc.Day];
        bool dayOfWeekMatches = _daysOfWeek[(int)utc.DayOfWeek];

        // The cron oddity worth spelling out: when BOTH day fields are
        // restricted they are ORed, not ANDed. "0 0 1 * 1" is the first of the
        // month AND every Monday, not Mondays that fall on the first. Getting
        // this wrong makes a monthly job nearly never fire.
        if (_dayOfMonthRestricted && _dayOfWeekRestricted)
        {
            return dayOfMonthMatches || dayOfWeekMatches;
        }

        return dayOfMonthMatches && dayOfWeekMatches;
    }

    private static void Fill(bool[] slots, string field, int min, int max)
    {
        foreach (string part in field.Split(','))
        {
            string range = part;
            int step = 1;

            int slash = range.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                step = int.Parse(
                    range.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture);
                range = range.Substring(0, slash);

                if (step < 1)
                {
                    throw new FormatException("A step must be at least one.");
                }
            }

            int from;
            int to;

            if (range == "*")
            {
                from = min;
                to = max;
            }
            else
            {
                int dash = range.IndexOf('-', StringComparison.Ordinal);

                if (dash >= 0)
                {
                    from = int.Parse(
                        range.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture);
                    to = int.Parse(
                        range.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture);
                }
                else
                {
                    from = int.Parse(range, NumberStyles.None, CultureInfo.InvariantCulture);
                    to = from;
                }
            }

            if (from < min || to > max || from > to)
            {
                throw new FormatException("A field value is outside its permitted range.");
            }

            for (int value = from; value <= to; value += step)
            {
                slots[value] = true;
            }
        }
    }
}
