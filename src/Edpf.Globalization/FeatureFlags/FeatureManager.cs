using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Edpf.Core.Guards;

namespace Edpf.Globalization.FeatureFlags;

/// <summary>
/// How a flag is targeted (Phase 28).
/// </summary>
public sealed class FeatureFlag
{
    /// <summary>
    /// Initializes a flag.
    /// </summary>
    /// <param name="name">Stable flag name.</param>
    /// <param name="enabled">The master switch. False is the kill switch.</param>
    /// <param name="rolloutPercentage">0–100. Applied only when <paramref name="enabled"/> is true.</param>
    /// <param name="enabledTenants">Tenants explicitly enabled regardless of the percentage.</param>
    /// <param name="disabledTenants">Tenants explicitly disabled regardless of everything else.</param>
    /// <param name="activeFrom">When the flag starts applying; null for immediately.</param>
    /// <param name="activeUntil">When it stops; null for indefinitely.</param>
    /// <exception cref="ArgumentOutOfRangeException">The percentage is outside 0–100.</exception>
    public FeatureFlag(
        string name,
        bool enabled,
        int rolloutPercentage = 100,
        IReadOnlyCollection<Guid>? enabledTenants = null,
        IReadOnlyCollection<Guid>? disabledTenants = null,
        DateTimeOffset? activeFrom = null,
        DateTimeOffset? activeUntil = null)
    {
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name));

        if (rolloutPercentage is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rolloutPercentage), rolloutPercentage, "Rollout percentage is 0-100.");
        }

        Enabled = enabled;
        RolloutPercentage = rolloutPercentage;
        EnabledTenants = enabledTenants ?? [];
        DisabledTenants = disabledTenants ?? [];
        ActiveFrom = activeFrom;
        ActiveUntil = activeUntil;
    }

    /// <summary>Stable flag name.</summary>
    public string Name { get; }

    /// <summary>
    /// The master switch. Setting it false is the incident-response kill
    /// switch and overrides every other targeting rule.
    /// </summary>
    public bool Enabled { get; }

    /// <summary>Percentage of tenants the flag applies to.</summary>
    public int RolloutPercentage { get; }

    /// <summary>Tenants enabled regardless of the percentage — pilot participants.</summary>
    public IReadOnlyCollection<Guid> EnabledTenants { get; }

    /// <summary>Tenants disabled regardless of everything else. Outranks the allow-list.</summary>
    public IReadOnlyCollection<Guid> DisabledTenants { get; }

    /// <summary>When the flag starts applying.</summary>
    public DateTimeOffset? ActiveFrom { get; }

    /// <summary>When it stops applying.</summary>
    public DateTimeOffset? ActiveUntil { get; }
}

/// <summary>
/// Evaluates feature flags (Phase 28).
/// </summary>
/// <remarks>
/// <para>
/// **Evaluation never makes a network call.** A flag check sits on the
/// request path, so a flag store reachable only over the network would put a
/// remote dependency in front of every request — turning the mechanism meant
/// to contain incidents into a cause of them. The store is snapshotted; the
/// evaluator reads the snapshot.
/// </para>
/// <para>
/// **Bucketing is stable.** A tenant's position is derived deterministically
/// from the flag name and tenant id, so a 10% rollout enables the *same* 10%
/// on every evaluation and every node. Random bucketing would let a tenant
/// flap in and out of a feature between two requests, which is worse than the
/// feature being off.
/// </para>
/// <para>
/// **Unknown flags are off.** An unreachable store or a typo'd name yields
/// the safe default rather than the exciting one.
/// </para>
/// </remarks>
public sealed class FeatureManager
{
    private readonly IReadOnlyDictionary<string, FeatureFlag> _snapshot;

    /// <summary>
    /// Initializes the evaluator over a flag snapshot.
    /// </summary>
    /// <param name="snapshot">
    /// The flags, already loaded. Refreshed out of band; never fetched during
    /// evaluation.
    /// </param>
    public FeatureManager(IReadOnlyDictionary<string, FeatureFlag> snapshot)
        => _snapshot = Guard.NotNull(snapshot, nameof(snapshot));

    /// <summary>An evaluator with no flags — everything off. The fail-safe state.</summary>
    public static FeatureManager Empty { get; } =
        new(new Dictionary<string, FeatureFlag>(StringComparer.Ordinal));

    /// <summary>
    /// Evaluates a flag for a tenant.
    /// </summary>
    /// <param name="flagName">The flag.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="now">The current instant, from <see cref="Abstractions.Primitives.IClock"/>.</param>
    /// <returns>True when the feature is on for this tenant, right now.</returns>
    public bool IsEnabled(string flagName, Guid tenantId, DateTimeOffset now)
    {
        Guard.NotNullOrWhiteSpace(flagName, nameof(flagName));

        // Unknown flag: off. Covers a typo, a removed flag, and a store that
        // failed to load — all of which must not turn a feature on.
        if (!_snapshot.TryGetValue(flagName, out FeatureFlag? flag))
        {
            return false;
        }

        // The kill switch, checked first so nothing can override it.
        if (!flag.Enabled)
        {
            return false;
        }

        if (flag.ActiveFrom is { } from && now < from)
        {
            return false;
        }

        if (flag.ActiveUntil is { } until && now >= until)
        {
            return false;
        }

        // Explicit deny outranks explicit allow: a tenant that hit a problem
        // is excluded even if it is also a pilot participant.
        if (Contains(flag.DisabledTenants, tenantId))
        {
            return false;
        }

        if (Contains(flag.EnabledTenants, tenantId))
        {
            return true;
        }

        return flag.RolloutPercentage switch
        {
            0 => false,
            100 => true,
            _ => BucketOf(flag.Name, tenantId) < flag.RolloutPercentage,
        };
    }

    /// <summary>
    /// The tenant's stable bucket (0–99) for a flag.
    /// </summary>
    /// <param name="flagName">The flag name — included so a tenant unlucky in
    /// one rollout is not unlucky in every rollout.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <returns>A stable value in 0–99.</returns>
    /// <remarks>
    /// FNV-1a rather than <see cref="string.GetHashCode()"/>: the framework
    /// hash is randomised per process, so buckets would differ between nodes
    /// and a tenant would see the feature on one server and off on another.
    /// </remarks>
    public static int BucketOf(string flagName, Guid tenantId)
    {
        Guard.NotNullOrWhiteSpace(flagName, nameof(flagName));

        string key = flagName + ":" + tenantId.ToString("N");
        byte[] bytes = Encoding.UTF8.GetBytes(key);

        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        uint hash = offsetBasis;
        foreach (byte b in bytes)
        {
            hash ^= b;
            hash *= prime;
        }

        return (int)(hash % 100);
    }

    private static bool Contains(IReadOnlyCollection<Guid> tenants, Guid tenantId)
    {
        foreach (Guid candidate in tenants)
        {
            if (candidate == tenantId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Flags in the snapshot that no longer appear in the supplied set of
    /// referenced names — stale flags (Phase 28 §"Verification").
    /// </summary>
    /// <param name="referencedFlagNames">Flag names the codebase still checks.</param>
    /// <returns>Flags that exist in configuration but are no longer read.</returns>
    /// <remarks>
    /// Stale flags accumulate into a configuration surface nobody understands,
    /// and an untested branch behind a forgotten flag is where incidents hide.
    /// </remarks>
    public IReadOnlyList<string> FindStaleFlags(IReadOnlyCollection<string> referencedFlagNames)
    {
        Guard.NotNull(referencedFlagNames, nameof(referencedFlagNames));

        var referenced = new HashSet<string>(referencedFlagNames, StringComparer.Ordinal);
        var stale = new List<string>();

        foreach (string name in _snapshot.Keys)
        {
            if (!referenced.Contains(name))
            {
                stale.Add(name);
            }
        }

        return stale;
    }
}
