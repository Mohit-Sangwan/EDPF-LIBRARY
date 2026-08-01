using System;
using Edpf.Core.Guards;

namespace Edpf.Operations.Slo;

/// <summary>
/// A service level objective and the error budget it implies (Phase 30).
/// </summary>
/// <remarks>
/// The error budget is the useful half. "99.95% availability" is an
/// aspiration; "21.9 minutes of unavailability per 30 days, of which 6 are
/// already spent" is a number a team can make decisions with — whether to
/// ship a risky change, whether to page someone, whether to stop feature work
/// and fix reliability.
/// </remarks>
public sealed class ServiceLevelObjective
{
    /// <summary>
    /// Initializes an objective.
    /// </summary>
    /// <param name="name">What this measures, e.g. <c>api-availability</c>.</param>
    /// <param name="target">
    /// The target as a fraction, e.g. 0.9995 for 99.95%. Must be greater than
    /// 0 and less than 1.
    /// </param>
    /// <param name="window">The rolling window the budget is measured over.</param>
    /// <param name="runbookUrl">
    /// Where an on-call engineer goes when this burns. **Required** — an
    /// alert without a runbook is an alert that cannot be acted on.
    /// </param>
    /// <param name="owner">
    /// The named owner. **Required** — an alert nobody owns is an alert
    /// nobody fixes.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">The target is not a fraction below 1.</exception>
    /// <exception cref="ArgumentException">The window is not positive, or a required string is blank.</exception>
    public ServiceLevelObjective(
        string name, double target, TimeSpan window, Uri runbookUrl, string owner)
    {
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name));

        if (target is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(target), target,
                "An SLO target is a fraction below 1. A target of 1 leaves no error budget, which means every "
                + "single failure is an incident and the budget stops being a decision-making tool.");
        }

        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentException("The measurement window must be positive.", nameof(window));
        }

        // Both are mandatory by Phase 30: "every alert must be actionable,
        // have a runbook, and have a named owner. An alert that cannot be
        // acted on is deleted."
        RunbookUrl = Guard.NotNull(runbookUrl, nameof(runbookUrl));
        Owner = Guard.NotNullOrWhiteSpace(owner, nameof(owner));

        Target = target;
        Window = window;
    }

    /// <summary>What this measures.</summary>
    public string Name { get; }

    /// <summary>The target as a fraction, e.g. 0.9995.</summary>
    public double Target { get; }

    /// <summary>The rolling measurement window.</summary>
    public TimeSpan Window { get; }

    /// <summary>Where an on-call engineer goes when this burns.</summary>
    public Uri RunbookUrl { get; }

    /// <summary>The named owner.</summary>
    public string Owner { get; }

    /// <summary>
    /// The fraction of requests that may fail before the objective is
    /// missed — 0.0005 for a 99.95% target.
    /// </summary>
    public double ErrorBudgetFraction => 1.0 - Target;

    /// <summary>
    /// The error budget expressed as time within the window: 21.9 minutes
    /// for 99.95% over 30 days.
    /// </summary>
    public TimeSpan ErrorBudgetDuration => TimeSpan.FromTicks((long)(Window.Ticks * ErrorBudgetFraction));
}
