using System;
using System.Collections.Generic;
using Edpf.Core.Guards;

namespace Edpf.Operations.SupplyChain;

/// <summary>What a licence permits for EDPF's purposes (Phase 34).</summary>
public enum LicenseDisposition
{
    /// <summary>Permissive; may appear anywhere including the core graph.</summary>
    Allowed = 0,

    /// <summary>
    /// Weak copyleft — acceptable only in an optional package a consumer opts
    /// into, never in the core graph (ADR-009).
    /// </summary>
    OptionalPackageOnly = 1,

    /// <summary>Strong copyleft or commercially restrictive; fails the build.</summary>
    Forbidden = 2,

    /// <summary>
    /// Not in the policy. **Treated as forbidden** until someone classifies
    /// it — an unclassified licence is one nobody has read.
    /// </summary>
    Unknown = 3,
}

/// <summary>One dependency and its declared licence.</summary>
public sealed class DependencyLicense
{
    /// <summary>
    /// Initializes a dependency record.
    /// </summary>
    /// <param name="packageId">The package.</param>
    /// <param name="version">Its version.</param>
    /// <param name="licenseExpression">The SPDX expression, e.g. <c>MIT</c>.</param>
    /// <param name="isTransitive">
    /// True when it arrived through another package. Transitive licences are
    /// where the surprises live, because nobody chose them.
    /// </param>
    public DependencyLicense(string packageId, string version, string? licenseExpression, bool isTransitive)
    {
        PackageId = Guard.NotNullOrWhiteSpace(packageId, nameof(packageId));
        Version = Guard.NotNullOrWhiteSpace(version, nameof(version));
        LicenseExpression = licenseExpression;
        IsTransitive = isTransitive;
    }

    /// <summary>The package.</summary>
    public string PackageId { get; }

    /// <summary>Its version.</summary>
    public string Version { get; }

    /// <summary>The SPDX expression, or null when the package declares none.</summary>
    public string? LicenseExpression { get; }

    /// <summary>True when it arrived through another package.</summary>
    public bool IsTransitive { get; }
}

/// <summary>
/// The licence-policy gate: a non-compliant transitive licence fails the
/// build (Phase 34 §"Supply-chain security").
/// </summary>
/// <remarks>
/// <para>
/// **Transitive dependencies are the point.** Nobody adds a strong-copyleft
/// package deliberately; it arrives four levels down a dependency chain
/// somebody added for a date formatter. By the time legal notices, it has
/// shipped, and the remediation is a re-release rather than a package swap.
/// </para>
/// <para>
/// **An unknown licence fails**, because an unclassified licence is one
/// nobody has read. Failing closed here costs a five-minute classification;
/// failing open costs a licence review after release.
/// </para>
/// </remarks>
public sealed class LicensePolicy
{
    private static readonly Dictionary<string, LicenseDisposition> DefaultPolicy =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Permissive: safe anywhere.
            ["MIT"] = LicenseDisposition.Allowed,
            ["Apache-2.0"] = LicenseDisposition.Allowed,
            ["BSD-2-Clause"] = LicenseDisposition.Allowed,
            ["BSD-3-Clause"] = LicenseDisposition.Allowed,
            ["ISC"] = LicenseDisposition.Allowed,
            ["MS-PL"] = LicenseDisposition.Allowed,
            ["Unlicense"] = LicenseDisposition.Allowed,

            // Weak copyleft: linking is fine, so an optional package a
            // consumer opts into is acceptable; the core graph is not.
            ["LGPL-2.1-only"] = LicenseDisposition.OptionalPackageOnly,
            ["LGPL-3.0-only"] = LicenseDisposition.OptionalPackageOnly,
            ["MPL-2.0"] = LicenseDisposition.OptionalPackageOnly,
            ["EPL-2.0"] = LicenseDisposition.OptionalPackageOnly,

            // Strong copyleft: would impose its terms on every consumer of
            // EDPF, which the dual-licence model (ADR-009) cannot accommodate.
            ["GPL-2.0-only"] = LicenseDisposition.Forbidden,
            ["GPL-3.0-only"] = LicenseDisposition.Forbidden,
            ["AGPL-3.0-only"] = LicenseDisposition.Forbidden,
            ["SSPL-1.0"] = LicenseDisposition.Forbidden,

            // ── deprecated SPDX identifiers ──────────────────────────────
            // SPDX deprecated the bare forms in favour of -only/-or-later,
            // but NuGet packages in the wild still declare them constantly.
            // Without these entries a known-forbidden licence reports as
            // "unclassified", which fails closed — safe, but it tells a
            // reader "nobody has read this licence" when the truth is
            // "remove this package". A wrong diagnosis on a correct verdict
            // still costs someone an afternoon.
            ["GPL-2.0"] = LicenseDisposition.Forbidden,
            ["GPL-3.0"] = LicenseDisposition.Forbidden,
            ["AGPL-3.0"] = LicenseDisposition.Forbidden,
            ["GPL-2.0-or-later"] = LicenseDisposition.Forbidden,
            ["GPL-3.0-or-later"] = LicenseDisposition.Forbidden,
            ["AGPL-3.0-or-later"] = LicenseDisposition.Forbidden,
            ["LGPL-2.1"] = LicenseDisposition.OptionalPackageOnly,
            ["LGPL-3.0"] = LicenseDisposition.OptionalPackageOnly,
            ["LGPL-2.1-or-later"] = LicenseDisposition.OptionalPackageOnly,
            ["LGPL-3.0-or-later"] = LicenseDisposition.OptionalPackageOnly,
            ["BSD-2"] = LicenseDisposition.Allowed,
            ["BSD-3"] = LicenseDisposition.Allowed,

            // Source-available but commercially restrictive.
            ["BUSL-1.1"] = LicenseDisposition.Forbidden,
            ["Elastic-2.0"] = LicenseDisposition.Forbidden,
        };

    private readonly IReadOnlyDictionary<string, LicenseDisposition> _policy;

    /// <summary>
    /// Initializes the gate.
    /// </summary>
    /// <param name="policy">Licence dispositions, or null for the default policy.</param>
    public LicensePolicy(IReadOnlyDictionary<string, LicenseDisposition>? policy = null)
        => _policy = policy ?? DefaultPolicy;

    /// <summary>
    /// Classifies a licence expression.
    /// </summary>
    /// <param name="licenseExpression">The SPDX expression.</param>
    /// <returns>Its disposition; <see cref="LicenseDisposition.Unknown"/> when unclassified.</returns>
    public LicenseDisposition Classify(string? licenseExpression)
        => string.IsNullOrWhiteSpace(licenseExpression)
            ? LicenseDisposition.Unknown
            : _policy.TryGetValue(licenseExpression!, out LicenseDisposition disposition)
                ? disposition
                : LicenseDisposition.Unknown;

    /// <summary>
    /// Evaluates a dependency graph.
    /// </summary>
    /// <param name="dependencies">Every dependency, direct and transitive.</param>
    /// <param name="isCorePackage">
    /// True when evaluating the core package graph, where
    /// <see cref="LicenseDisposition.OptionalPackageOnly"/> is also a
    /// violation (ADR-009: the core ships licence-clean).
    /// </param>
    /// <returns>Every violation. Empty means the build may proceed.</returns>
    public IReadOnlyList<LicenseViolation> Evaluate(
        IReadOnlyCollection<DependencyLicense> dependencies, bool isCorePackage)
    {
        Guard.NotNull(dependencies, nameof(dependencies));

        var violations = new List<LicenseViolation>();

        foreach (DependencyLicense dependency in dependencies)
        {
            LicenseDisposition disposition = Classify(dependency.LicenseExpression);

            string? reason = disposition switch
            {
                LicenseDisposition.Forbidden =>
                    "Licence is forbidden by policy; it would impose its terms on every EDPF consumer.",

                LicenseDisposition.Unknown =>
                    "Licence is unclassified. An unclassified licence is one nobody has read, so it fails "
                    + "closed until someone classifies it.",

                LicenseDisposition.OptionalPackageOnly when isCorePackage =>
                    "Weak-copyleft licence in the core package graph. Permitted only in an optional package "
                    + "a consumer opts into (ADR-009).",

                _ => null,
            };

            if (reason is not null)
            {
                violations.Add(new LicenseViolation(dependency, disposition, reason));
            }
        }

        return violations;
    }
}

/// <summary>One licence-policy violation.</summary>
public sealed class LicenseViolation
{
    /// <summary>
    /// Initializes a violation.
    /// </summary>
    /// <param name="dependency">The offending dependency.</param>
    /// <param name="disposition">How its licence was classified.</param>
    /// <param name="reason">Why it fails, in terms a reviewer can act on.</param>
    public LicenseViolation(DependencyLicense dependency, LicenseDisposition disposition, string reason)
    {
        Dependency = Guard.NotNull(dependency, nameof(dependency));
        Disposition = disposition;
        Reason = Guard.NotNull(reason, nameof(reason));
    }

    /// <summary>The offending dependency.</summary>
    public DependencyLicense Dependency { get; }

    /// <summary>How its licence was classified.</summary>
    public LicenseDisposition Disposition { get; }

    /// <summary>Why it fails.</summary>
    public string Reason { get; }

    /// <summary>Formats as <c>package version (licence): reason</c>, plus how it arrived.</summary>
    public override string ToString()
        => $"{Dependency.PackageId} {Dependency.Version} "
         + $"({Dependency.LicenseExpression ?? "no licence declared"})"
         + $"{(Dependency.IsTransitive ? " [transitive]" : string.Empty)}: {Reason}";
}
