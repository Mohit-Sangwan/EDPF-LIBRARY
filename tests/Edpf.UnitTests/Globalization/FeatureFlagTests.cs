using Edpf.Globalization.FeatureFlags;

namespace Edpf.UnitTests.Globalization;

/// <summary>
/// Phase 28 §"Verification": targeting-rule correctness, stale-flag
/// detection, and fail-safe default when the flag store is unreachable.
/// </summary>
public sealed class FeatureFlagTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private static FeatureManager With(params FeatureFlag[] flags)
        => new(flags.ToDictionary(f => f.Name, f => f, StringComparer.Ordinal));

    [Fact]
    public void IsEnabled_UnknownFlag_IsOff()
    {
        // A typo, a removed flag, or a store that failed to load must not
        // turn a feature on.
        Assert.False(FeatureManager.Empty.IsEnabled("anything", TenantA, Now));
    }

    [Fact]
    public void IsEnabled_StoreUnreachable_FailsSafe()
    {
        // The empty snapshot is what a failed load produces.
        Assert.False(FeatureManager.Empty.IsEnabled("new-encryption-algorithm", TenantA, Now));
    }

    [Fact]
    public void IsEnabled_KillSwitchOff_OverridesEveryOtherRule()
    {
        // Incident response: setting Enabled false must beat an explicit
        // tenant allow-list and a 100% rollout.
        FeatureManager manager = With(new FeatureFlag(
            "risky", enabled: false, rolloutPercentage: 100, enabledTenants: [TenantA]));

        Assert.False(manager.IsEnabled("risky", TenantA, Now));
    }

    [Fact]
    public void IsEnabled_ExplicitDeny_OutranksExplicitAllow()
    {
        // A tenant that hit a problem is excluded even while it is still
        // listed as a pilot participant.
        FeatureManager manager = With(new FeatureFlag(
            "pilot", enabled: true, enabledTenants: [TenantA], disabledTenants: [TenantA]));

        Assert.False(manager.IsEnabled("pilot", TenantA, Now));
    }

    [Fact]
    public void IsEnabled_ExplicitAllow_BeatsThePercentage()
    {
        FeatureManager manager = With(new FeatureFlag(
            "pilot", enabled: true, rolloutPercentage: 0, enabledTenants: [TenantA]));

        Assert.True(manager.IsEnabled("pilot", TenantA, Now));
        Assert.False(manager.IsEnabled("pilot", TenantB, Now));
    }

    [Fact]
    public void IsEnabled_BeforeActiveFrom_IsOff()
    {
        FeatureManager manager = With(new FeatureFlag(
            "scheduled", enabled: true, activeFrom: Now.AddHours(1)));

        Assert.False(manager.IsEnabled("scheduled", TenantA, Now));
        Assert.True(manager.IsEnabled("scheduled", TenantA, Now.AddHours(2)));
    }

    [Fact]
    public void IsEnabled_AfterActiveUntil_IsOff()
    {
        FeatureManager manager = With(new FeatureFlag(
            "expiring", enabled: true, activeUntil: Now.AddHours(1)));

        Assert.True(manager.IsEnabled("expiring", TenantA, Now));
        Assert.False(manager.IsEnabled("expiring", TenantA, Now.AddHours(2)));
    }

    // ── stable bucketing ───────────────────────────────────────────────────

    [Fact]
    public void BucketOf_SameTenantAndFlag_IsAlwaysTheSame()
    {
        // A tenant flapping in and out of a feature between two requests is
        // worse than the feature being off.
        int first = FeatureManager.BucketOf("rollout", TenantA);

        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(first, FeatureManager.BucketOf("rollout", TenantA));
        }
    }

    [Fact]
    public void BucketOf_DifferentFlags_GiveIndependentBuckets()
    {
        // Including the flag name means a tenant unlucky in one rollout is
        // not unlucky in every rollout.
        int a = FeatureManager.BucketOf("rollout-a", TenantA);
        int b = FeatureManager.BucketOf("rollout-b", TenantA);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void BucketOf_Always_IsWithinRange()
    {
        for (int i = 0; i < 500; i++)
        {
            int bucket = FeatureManager.BucketOf("flag", Guid.NewGuid());
            Assert.InRange(bucket, 0, 99);
        }
    }

    [Fact]
    public void BucketOf_AcrossManyTenants_IsReasonablyUniform()
    {
        // A skewed hash would make "10%" mean something else entirely.
        var counts = new int[10];
        for (int i = 0; i < 10_000; i++)
        {
            counts[FeatureManager.BucketOf("flag", Guid.NewGuid()) / 10]++;
        }

        Assert.All(counts, count => Assert.InRange(count, 700, 1300));
    }

    [Fact]
    public void IsEnabled_TenPercentRollout_IsStableAcrossEvaluations()
    {
        FeatureManager manager = With(new FeatureFlag("gradual", enabled: true, rolloutPercentage: 10));
        var tenants = Enumerable.Range(0, 200).Select(_ => Guid.NewGuid()).ToArray();

        bool[] firstPass = tenants.Select(t => manager.IsEnabled("gradual", t, Now)).ToArray();
        bool[] secondPass = tenants.Select(t => manager.IsEnabled("gradual", t, Now.AddHours(3))).ToArray();

        Assert.Equal(firstPass, secondPass);
    }

    [Fact]
    public void IsEnabled_IncreasingThePercentage_OnlyAddsTenants()
    {
        // Progressive rollout must be monotonic: going 1% → 10% must not
        // remove the feature from anyone who already had it.
        var tenants = Enumerable.Range(0, 300).Select(_ => Guid.NewGuid()).ToArray();
        FeatureManager onePercent = With(new FeatureFlag("gradual", enabled: true, rolloutPercentage: 1));
        FeatureManager tenPercent = With(new FeatureFlag("gradual", enabled: true, rolloutPercentage: 10));

        foreach (Guid tenant in tenants)
        {
            if (onePercent.IsEnabled("gradual", tenant, Now))
            {
                Assert.True(
                    tenPercent.IsEnabled("gradual", tenant, Now),
                    "Widening a rollout must never withdraw the feature from a tenant that already had it.");
            }
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(100, true)]
    public void IsEnabled_BoundaryPercentages_AreAbsolute(int percentage, bool expected)
    {
        FeatureManager manager = With(new FeatureFlag("edge", enabled: true, rolloutPercentage: percentage));

        Assert.Equal(expected, manager.IsEnabled("edge", TenantA, Now));
    }

    [Fact]
    public void Constructor_PercentageOutOfRange_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FeatureFlag("f", true, rolloutPercentage: 101));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FeatureFlag("f", true, rolloutPercentage: -1));
    }

    [Fact]
    public void FindStaleFlags_ConfiguredButNoLongerReferenced_IsReported()
    {
        // An untested branch behind a forgotten flag is where incidents hide.
        FeatureManager manager = With(
            new FeatureFlag("still-used", true),
            new FeatureFlag("forgotten-2019", true));

        IReadOnlyList<string> stale = manager.FindStaleFlags(["still-used"]);

        Assert.Equal("forgotten-2019", Assert.Single(stale));
    }

    [Fact]
    public void FindStaleFlags_AllReferenced_ReportsNothing()
    {
        FeatureManager manager = With(new FeatureFlag("a", true), new FeatureFlag("b", true));

        Assert.Empty(manager.FindStaleFlags(["a", "b"]));
    }
}
