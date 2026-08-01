using System;
using System.Collections.Generic;
using Edpf.Core.Guards;

namespace Edpf.Operations.Slo;

/// <summary>How urgently a burn-rate condition needs a human.</summary>
public enum AlertSeverity
{
    /// <summary>Nothing to do.</summary>
    None = 0,

    /// <summary>Raise a ticket. The budget will be exhausted well before the window ends.</summary>
    Ticket = 1,

    /// <summary>Page someone now. The budget will be gone within hours.</summary>
    Page = 2,
}

/// <summary>One window of a multi-window burn-rate alert.</summary>
public sealed class BurnRateWindow
{
    /// <summary>
    /// Initializes a window.
    /// </summary>
    /// <param name="longWindow">The detection window.</param>
    /// <param name="shortWindow">
    /// The confirmation window, typically one twelfth of the long window. It
    /// exists to make the alert **stop firing** promptly once the burn does.
    /// </param>
    /// <param name="burnRateThreshold">Multiples of the budget-consuming rate that trip this window.</param>
    /// <param name="severity">What to do when it trips.</param>
    /// <exception cref="ArgumentException">The windows are non-positive or mis-ordered.</exception>
    public BurnRateWindow(
        TimeSpan longWindow, TimeSpan shortWindow, double burnRateThreshold, AlertSeverity severity)
    {
        if (longWindow <= TimeSpan.Zero || shortWindow <= TimeSpan.Zero)
        {
            throw new ArgumentException("Burn-rate windows must be positive.", nameof(longWindow));
        }

        if (shortWindow >= longWindow)
        {
            throw new ArgumentException(
                "The short window must be shorter than the long window; it exists to confirm the burn is "
                + "still happening.",
                nameof(shortWindow));
        }

        if (burnRateThreshold <= 0)
        {
            throw new ArgumentException("The burn-rate threshold must be positive.", nameof(burnRateThreshold));
        }

        LongWindow = longWindow;
        ShortWindow = shortWindow;
        BurnRateThreshold = burnRateThreshold;
        Severity = severity;
    }

    /// <summary>The detection window.</summary>
    public TimeSpan LongWindow { get; }

    /// <summary>The confirmation window.</summary>
    public TimeSpan ShortWindow { get; }

    /// <summary>Multiples of the budget-consuming rate that trip this window.</summary>
    public double BurnRateThreshold { get; }

    /// <summary>What to do when it trips.</summary>
    public AlertSeverity Severity { get; }
}

/// <summary>
/// Multi-window, multi-burn-rate SLO alerting (Phase 30).
/// </summary>
/// <remarks>
/// <para>
/// **Why not a threshold alert.** "Alert when the error rate exceeds 1%" is
/// the alert that fails on-call rotations in both directions at once: it
/// pages at 3 a.m. for a ten-second blip that consumed 0.01% of the budget,
/// and it stays silent through a slow 0.9% burn that will exhaust the budget
/// by Thursday. Engineers then learn to ignore it, which is how a real
/// incident gets missed. Alert fatigue is a security risk.
/// </para>
/// <para>
/// **Burn rate** fixes the first problem: it measures error rate as a
/// multiple of the rate that would exactly consume the budget over the whole
/// window. A burn rate of 1 means the budget runs out exactly at the window's
/// end; 14.4 means it runs out in about two hours of a 30-day window.
/// </para>
/// <para>
/// **Multiple windows** fix the second. A fast window (1 h) at a high burn
/// rate pages for genuine emergencies; a slow window (6 h, 3 d) at a lower
/// burn rate raises a ticket for the quiet drift that would otherwise go
/// unnoticed until the budget is gone.
/// </para>
/// <para>
/// **The short confirmation window** is what makes an alert resolve. Without
/// it, a five-minute outage keeps a one-hour-window alert firing for a full
/// hour after recovery, and on-call learns to ignore the resolution too.
/// </para>
/// </remarks>
public sealed class BurnRateEvaluator
{
    private readonly ServiceLevelObjective _objective;
    private readonly IReadOnlyList<BurnRateWindow> _windows;

    /// <summary>
    /// Initializes the evaluator.
    /// </summary>
    /// <param name="objective">The objective being protected.</param>
    /// <param name="windows">The alerting windows, most urgent first.</param>
    public BurnRateEvaluator(ServiceLevelObjective objective, IReadOnlyList<BurnRateWindow> windows)
    {
        _objective = Guard.NotNull(objective, nameof(objective));
        _windows = Guard.NotNull(windows, nameof(windows));
    }

    /// <summary>
    /// The Google SRE workbook's standard windows for a 30-day objective:
    /// page at 14.4× over an hour (2% of budget), page at 6× over six hours
    /// (5%), ticket at 3× over a day (10%), ticket at 1× over three days
    /// (10%).
    /// </summary>
    /// <param name="objective">The objective to protect.</param>
    /// <returns>An evaluator with the standard four windows.</returns>
    public static BurnRateEvaluator Standard(ServiceLevelObjective objective)
        => new(objective,
        [
            new BurnRateWindow(TimeSpan.FromHours(1), TimeSpan.FromMinutes(5), 14.4, AlertSeverity.Page),
            new BurnRateWindow(TimeSpan.FromHours(6), TimeSpan.FromMinutes(30), 6.0, AlertSeverity.Page),
            new BurnRateWindow(TimeSpan.FromDays(1), TimeSpan.FromHours(2), 3.0, AlertSeverity.Ticket),
            new BurnRateWindow(TimeSpan.FromDays(3), TimeSpan.FromHours(6), 1.0, AlertSeverity.Ticket),
        ]);

    /// <summary>
    /// Burn rate for an observed error fraction: the multiple of the budget
    /// this rate consumes.
    /// </summary>
    /// <param name="observedErrorFraction">Errors ÷ total, in the window.</param>
    /// <returns>The burn rate. 1.0 exhausts the budget exactly at the window's end.</returns>
    public double BurnRate(double observedErrorFraction)
    {
        if (observedErrorFraction < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(observedErrorFraction), observedErrorFraction, "An error fraction cannot be negative.");
        }

        return observedErrorFraction / _objective.ErrorBudgetFraction;
    }

    /// <summary>
    /// Evaluates every window and returns the most urgent alert warranted.
    /// </summary>
    /// <param name="errorFractionByLongWindow">Observed error fraction per long window.</param>
    /// <param name="errorFractionByShortWindow">Observed error fraction per short window.</param>
    /// <returns>
    /// The alert to raise, or a <see cref="AlertSeverity.None"/> decision.
    /// A window fires only when **both** its windows exceed the threshold, so
    /// the alert clears promptly once the burn stops.
    /// </returns>
    public BurnRateDecision Evaluate(
        IReadOnlyDictionary<TimeSpan, double> errorFractionByLongWindow,
        IReadOnlyDictionary<TimeSpan, double> errorFractionByShortWindow)
    {
        Guard.NotNull(errorFractionByLongWindow, nameof(errorFractionByLongWindow));
        Guard.NotNull(errorFractionByShortWindow, nameof(errorFractionByShortWindow));

        foreach (BurnRateWindow window in _windows)
        {
            if (!errorFractionByLongWindow.TryGetValue(window.LongWindow, out double longFraction)
                || !errorFractionByShortWindow.TryGetValue(window.ShortWindow, out double shortFraction))
            {
                continue;
            }

            bool longTripped = BurnRate(longFraction) >= window.BurnRateThreshold;
            bool shortTripped = BurnRate(shortFraction) >= window.BurnRateThreshold;

            if (longTripped && shortTripped)
            {
                return new BurnRateDecision(
                    window.Severity,
                    window,
                    BurnRate(longFraction),
                    _objective.RunbookUrl,
                    _objective.Owner);
            }
        }

        return BurnRateDecision.NoAlert;
    }

    /// <summary>
    /// How much of the budget remains, as a fraction of the whole budget.
    /// </summary>
    /// <param name="observedErrorFraction">Errors ÷ total over the full window so far.</param>
    /// <returns>
    /// 1.0 for an untouched budget, 0.0 for exhausted. Negative values are
    /// clamped to zero — a budget cannot be more than spent, and reporting
    /// -30% invites arguing about the number instead of fixing the service.
    /// </returns>
    public double RemainingBudgetFraction(double observedErrorFraction)
        => Math.Max(0.0, 1.0 - BurnRate(observedErrorFraction));
}

/// <summary>The outcome of a burn-rate evaluation.</summary>
public sealed class BurnRateDecision
{
    internal BurnRateDecision(
        AlertSeverity severity, BurnRateWindow? window, double burnRate, Uri? runbookUrl, string? owner)
    {
        Severity = severity;
        Window = window;
        BurnRate = burnRate;
        RunbookUrl = runbookUrl;
        Owner = owner;
    }

    /// <summary>No alert warranted.</summary>
    public static BurnRateDecision NoAlert { get; } = new(AlertSeverity.None, null, 0, null, null);

    /// <summary>How urgent.</summary>
    public AlertSeverity Severity { get; }

    /// <summary>Which window tripped.</summary>
    public BurnRateWindow? Window { get; }

    /// <summary>The observed burn rate.</summary>
    public double BurnRate { get; }

    /// <summary>Where the on-call engineer goes. Always present on a firing alert.</summary>
    public Uri? RunbookUrl { get; }

    /// <summary>Who owns it. Always present on a firing alert.</summary>
    public string? Owner { get; }

    /// <summary>True when a human needs to be told.</summary>
    public bool ShouldAlert => Severity != AlertSeverity.None;
}
