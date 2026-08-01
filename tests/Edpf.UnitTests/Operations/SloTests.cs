using Edpf.Operations.Slo;

namespace Edpf.UnitTests.Operations;

/// <summary>
/// Phase 30 §"Verification": SLO calculation accuracy, and the multi-window
/// multi-burn-rate behaviour that naive threshold alerts get wrong.
/// </summary>
public sealed class ServiceLevelObjectiveTests
{
    private static readonly Uri Runbook = new("https://runbooks.edpf.dev/api-availability");

    private static ServiceLevelObjective Availability(double target = 0.9995) =>
        new("api-availability", target, TimeSpan.FromDays(30), Runbook, "platform-squad");

    [Fact]
    public void ErrorBudgetDuration_NinetyNinePointNineFive_IsAboutTwentyTwoMinutes()
    {
        // The number a team can act on: "we have 21.9 minutes this month."
        TimeSpan budget = Availability().ErrorBudgetDuration;

        Assert.Equal(21.6, budget.TotalMinutes, precision: 1);
    }

    [Theory]
    [InlineData(0.99, 7.2)]      // 99%    -> 7.2 hours per 30 days
    [InlineData(0.999, 43.2)]    // 99.9%  -> 43.2 minutes
    [InlineData(0.9999, 4.32)]   // 99.99% -> 4.32 minutes
    public void ErrorBudgetDuration_ScalesWithTheTarget(double target, double expectedMagnitude)
    {
        TimeSpan budget = Availability(target).ErrorBudgetDuration;

        double actual = target switch
        {
            0.99 => budget.TotalHours,
            _ => budget.TotalMinutes,
        };

        Assert.Equal(expectedMagnitude, actual, precision: 1);
    }

    [Fact]
    public void Constructor_TargetOfOne_IsRejected()
    {
        // A 100% target leaves no budget, so every single failure becomes an
        // incident and the budget stops being a decision-making tool.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ServiceLevelObjective("x", 1.0, TimeSpan.FromDays(30), Runbook, "owner"));
    }

    [Fact]
    public void Constructor_WithoutRunbookOrOwner_IsRejected()
    {
        // Phase 30: "every alert must be actionable, have a runbook, and have
        // a named owner. An alert that cannot be acted on is deleted."
        Assert.Throws<ArgumentNullException>(
            () => new ServiceLevelObjective("x", 0.999, TimeSpan.FromDays(30), null!, "owner"));

        Assert.Throws<ArgumentException>(
            () => new ServiceLevelObjective("x", 0.999, TimeSpan.FromDays(30), Runbook, "  "));
    }
}

/// <summary>Multi-window, multi-burn-rate alerting.</summary>
public sealed class BurnRateEvaluatorTests
{
    private static readonly Uri Runbook = new("https://runbooks.edpf.dev/api-availability");

    private static ServiceLevelObjective Objective =>
        new("api-availability", 0.999, TimeSpan.FromDays(30), Runbook, "platform-squad");

    private static BurnRateEvaluator Evaluator => BurnRateEvaluator.Standard(Objective);

    [Fact]
    public void BurnRate_ExactlyTheBudgetRate_IsOne()
    {
        // 0.1% errors against a 0.1% budget: the budget runs out precisely at
        // the window's end.
        Assert.Equal(1.0, Evaluator.BurnRate(0.001), precision: 6);
    }

    [Fact]
    public void BurnRate_TenTimesTheBudgetRate_IsTen()
        => Assert.Equal(10.0, Evaluator.BurnRate(0.01), precision: 6);

    [Fact]
    public void BurnRate_NoErrors_IsZero()
        => Assert.Equal(0.0, Evaluator.BurnRate(0.0));

    [Fact]
    public void RemainingBudgetFraction_HalfSpent_IsHalf()
        => Assert.Equal(0.5, Evaluator.RemainingBudgetFraction(0.0005), precision: 6);

    [Fact]
    public void RemainingBudgetFraction_Overspent_IsClampedToZero()
    {
        // Reporting -30% invites arguing about the number instead of fixing
        // the service.
        Assert.Equal(0.0, Evaluator.RemainingBudgetFraction(0.05));
    }

    // ── the behaviour naive thresholds get wrong ───────────────────────────

    [Fact]
    public void Evaluate_BriefSpikeThatBarelyTouchesTheBudget_DoesNotPage()
    {
        // The 3 a.m. false page. A short blip raises the one-hour rate but
        // the longer windows stay quiet, so nobody is woken.
        BurnRateDecision decision = Evaluator.Evaluate(
            errorFractionByLongWindow: new Dictionary<TimeSpan, double>
            {
                [TimeSpan.FromHours(1)] = 0.002,   // 2x burn — well under 14.4
                [TimeSpan.FromHours(6)] = 0.0002,
                [TimeSpan.FromDays(1)] = 0.0001,
                [TimeSpan.FromDays(3)] = 0.00005,
            },
            errorFractionByShortWindow: new Dictionary<TimeSpan, double>
            {
                [TimeSpan.FromMinutes(5)] = 0.002,
                [TimeSpan.FromMinutes(30)] = 0.0002,
                [TimeSpan.FromHours(2)] = 0.0001,
                [TimeSpan.FromHours(6)] = 0.00005,
            });

        Assert.False(decision.ShouldAlert);
    }

    [Fact]
    public void Evaluate_SevereOngoingOutage_PagesImmediately()
    {
        // 2% errors is a 20x burn: the month's budget is gone in about 36
        // hours, so this pages on the one-hour window.
        BurnRateDecision decision = Evaluator.Evaluate(
            errorFractionByLongWindow: new Dictionary<TimeSpan, double> { [TimeSpan.FromHours(1)] = 0.02 },
            errorFractionByShortWindow: new Dictionary<TimeSpan, double> { [TimeSpan.FromMinutes(5)] = 0.02 });

        Assert.Equal(AlertSeverity.Page, decision.Severity);
        Assert.Equal(20.0, decision.BurnRate, precision: 4);
    }

    [Fact]
    public void Evaluate_SlowQuietBurn_RaisesATicketRatherThanBeingIgnored()
    {
        // The failure a threshold alert misses entirely: 0.35% errors is well
        // under any "alert above 1%" rule, but it is a 3.5x burn that
        // exhausts the month's budget in about nine days.
        BurnRateDecision decision = Evaluator.Evaluate(
            errorFractionByLongWindow: new Dictionary<TimeSpan, double>
            {
                [TimeSpan.FromHours(1)] = 0.0035,
                [TimeSpan.FromHours(6)] = 0.0035,
                [TimeSpan.FromDays(1)] = 0.0035,
            },
            errorFractionByShortWindow: new Dictionary<TimeSpan, double>
            {
                [TimeSpan.FromMinutes(5)] = 0.0035,
                [TimeSpan.FromMinutes(30)] = 0.0035,
                [TimeSpan.FromHours(2)] = 0.0035,
            });

        Assert.Equal(AlertSeverity.Ticket, decision.Severity);
    }

    [Fact]
    public void Evaluate_BurnStoppedButLongWindowStillElevated_DoesNotKeepFiring()
    {
        // The confirmation window earning its place. After recovery the long
        // window still carries the outage, but the short one is clean, so the
        // alert resolves instead of nagging for another hour.
        BurnRateDecision decision = Evaluator.Evaluate(
            errorFractionByLongWindow: new Dictionary<TimeSpan, double> { [TimeSpan.FromHours(1)] = 0.02 },
            errorFractionByShortWindow: new Dictionary<TimeSpan, double> { [TimeSpan.FromMinutes(5)] = 0.0 });

        Assert.False(decision.ShouldAlert);
    }

    [Fact]
    public void Evaluate_FiringAlert_AlwaysCarriesRunbookAndOwner()
    {
        // An alert an engineer cannot act on is an alert that gets ignored.
        BurnRateDecision decision = Evaluator.Evaluate(
            errorFractionByLongWindow: new Dictionary<TimeSpan, double> { [TimeSpan.FromHours(1)] = 0.02 },
            errorFractionByShortWindow: new Dictionary<TimeSpan, double> { [TimeSpan.FromMinutes(5)] = 0.02 });

        Assert.NotNull(decision.RunbookUrl);
        Assert.Equal("platform-squad", decision.Owner);
    }

    [Fact]
    public void Evaluate_MostUrgentWindowWins()
    {
        // Both the page and ticket windows trip; the engineer gets paged
        // rather than receiving a ticket about an ongoing outage.
        BurnRateDecision decision = Evaluator.Evaluate(
            errorFractionByLongWindow: new Dictionary<TimeSpan, double>
            {
                [TimeSpan.FromHours(1)] = 0.02,
                [TimeSpan.FromDays(1)] = 0.02,
            },
            errorFractionByShortWindow: new Dictionary<TimeSpan, double>
            {
                [TimeSpan.FromMinutes(5)] = 0.02,
                [TimeSpan.FromHours(2)] = 0.02,
            });

        Assert.Equal(AlertSeverity.Page, decision.Severity);
    }

    [Fact]
    public void Evaluate_NoDataForAWindow_SkipsItRatherThanAlerting()
    {
        // Missing telemetry must not manufacture an alert; it is its own
        // problem, detected elsewhere.
        BurnRateDecision decision = Evaluator.Evaluate(
            errorFractionByLongWindow: new Dictionary<TimeSpan, double>(),
            errorFractionByShortWindow: new Dictionary<TimeSpan, double>());

        Assert.False(decision.ShouldAlert);
    }

    [Fact]
    public void BurnRateWindow_ShortNotShorterThanLong_IsRejected()
    {
        Assert.Throws<ArgumentException>(
            () => new BurnRateWindow(TimeSpan.FromHours(1), TimeSpan.FromHours(2), 1.0, AlertSeverity.Page));
    }
}
