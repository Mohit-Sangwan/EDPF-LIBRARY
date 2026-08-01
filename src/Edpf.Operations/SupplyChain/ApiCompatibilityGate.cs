using System;
using System.Collections.Generic;
using Edpf.Core.Guards;

namespace Edpf.Operations.SupplyChain;

/// <summary>The SemVer bump a set of API changes requires.</summary>
public enum RequiredVersionBump
{
    /// <summary>No public API change. A patch suffices.</summary>
    Patch = 0,

    /// <summary>API was added only. Minor.</summary>
    Minor = 1,

    /// <summary>API was removed or changed. Major.</summary>
    Major = 2,
}

/// <summary>
/// Diffs the Phase 01 public-API baselines and determines the SemVer bump
/// the change requires (Phase 34).
/// </summary>
/// <remarks>
/// <para>
/// **Why this is mechanical.** "Is this breaking?" answered by judgement is
/// answered inconsistently, usually under release pressure, and usually
/// optimistically. The baselines record the exact public surface, so the
/// question becomes a set difference — and a breaking change shipped under a
/// minor bump breaks consumers who followed SemVer and pinned accordingly.
/// </para>
/// <para>
/// **A removal and a signature change are the same thing here.** The baseline
/// records full signatures, so changing a parameter type removes one entry and
/// adds another; the removal is what makes it major. That is correct — a
/// consumer compiled against the old signature does not care that something
/// similarly named still exists.
/// </para>
/// </remarks>
public sealed class ApiCompatibilityGate
{
    /// <summary>
    /// Compares two public-API baselines.
    /// </summary>
    /// <param name="previous">The shipped baseline.</param>
    /// <param name="current">The baseline for the change under review.</param>
    /// <returns>What changed, and the bump it requires.</returns>
    public static ApiDiff Compare(
        IReadOnlyCollection<string> previous, IReadOnlyCollection<string> current)
    {
        Guard.NotNull(previous, nameof(previous));
        Guard.NotNull(current, nameof(current));

        var before = new HashSet<string>(previous, StringComparer.Ordinal);
        var after = new HashSet<string>(current, StringComparer.Ordinal);

        var removed = new List<string>();
        foreach (string entry in before)
        {
            if (!IsDirective(entry) && !after.Contains(entry))
            {
                removed.Add(entry);
            }
        }

        var added = new List<string>();
        foreach (string entry in after)
        {
            if (!IsDirective(entry) && !before.Contains(entry))
            {
                added.Add(entry);
            }
        }

        removed.Sort(StringComparer.Ordinal);
        added.Sort(StringComparer.Ordinal);

        RequiredVersionBump bump = removed.Count > 0
            ? RequiredVersionBump.Major
            : added.Count > 0
                ? RequiredVersionBump.Minor
                : RequiredVersionBump.Patch;

        return new ApiDiff(added, removed, bump);
    }

    /// <summary>
    /// Whether a proposed version bump is sufficient for the change.
    /// </summary>
    /// <param name="diff">The API diff.</param>
    /// <param name="proposedBump">The bump the release proposes.</param>
    /// <returns>
    /// True when the proposal is at least what the change requires.
    /// A larger bump than required is always allowed — over-signalling a
    /// change is never harmful, under-signalling breaks consumers.
    /// </returns>
    public static bool IsSufficient(ApiDiff diff, RequiredVersionBump proposedBump)
    {
        Guard.NotNull(diff, nameof(diff));
        return proposedBump >= diff.RequiredBump;
    }

    // The baseline files carry '#nullable enable' and may carry blank lines;
    // neither is API.
    private static bool IsDirective(string entry)
        => string.IsNullOrWhiteSpace(entry) || entry[0] == '#';
}

/// <summary>What changed between two public-API baselines.</summary>
public sealed class ApiDiff
{
    /// <summary>
    /// Initializes a diff.
    /// </summary>
    /// <param name="added">Symbols present now and not before.</param>
    /// <param name="removed">Symbols present before and not now.</param>
    /// <param name="requiredBump">The bump these changes require.</param>
    public ApiDiff(
        IReadOnlyList<string> added, IReadOnlyList<string> removed, RequiredVersionBump requiredBump)
    {
        Added = Guard.NotNull(added, nameof(added));
        Removed = Guard.NotNull(removed, nameof(removed));
        RequiredBump = requiredBump;
    }

    /// <summary>Symbols added.</summary>
    public IReadOnlyList<string> Added { get; }

    /// <summary>
    /// Symbols removed. Non-empty means major, and each entry is a consumer
    /// whose code will not compile.
    /// </summary>
    public IReadOnlyList<string> Removed { get; }

    /// <summary>The bump these changes require.</summary>
    public RequiredVersionBump RequiredBump { get; }

    /// <summary>True when the public surface is unchanged.</summary>
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0;
}
