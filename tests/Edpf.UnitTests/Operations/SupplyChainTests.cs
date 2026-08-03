using Edpf.Operations.SupplyChain;

namespace Edpf.UnitTests.Operations;

/// <summary>
/// Phase 34: "a non-compliant transitive licence fails the build".
/// </summary>
public sealed class LicensePolicyTests
{
    private static DependencyLicense Dep(string licence, bool transitive = true)
        => new("Some.Package", "1.0.0", licence, transitive);

    [Theory]
    [InlineData("MIT")]
    [InlineData("Apache-2.0")]
    [InlineData("BSD-3-Clause")]
    public void Evaluate_PermissiveLicence_PassesEverywhere(string licence)
    {
        Assert.Empty(new LicensePolicy().Evaluate([Dep(licence)], isCorePackage: true));
    }

    [Theory]
    [InlineData("GPL-3.0-only")]
    [InlineData("AGPL-3.0-only")]
    [InlineData("SSPL-1.0")]
    public void Evaluate_StrongCopyleft_FailsEvenInAnOptionalPackage(string licence)
    {
        // It would impose its terms on every EDPF consumer, which the
        // dual-licence model cannot accommodate.
        Assert.Single(new LicensePolicy().Evaluate([Dep(licence)], isCorePackage: false));
    }

    [Theory]
    // SPDX deprecated the bare forms, but NuGet packages declare them
    // constantly. Without these the gate still failed closed — safe — while
    // reporting "unclassified: nobody has read this licence" for a licence
    // that is in fact known-forbidden. A wrong diagnosis on a correct verdict
    // still costs someone an afternoon.
    [InlineData("GPL-2.0")]
    [InlineData("GPL-3.0")]
    [InlineData("AGPL-3.0")]
    [InlineData("GPL-3.0-or-later")]
    [InlineData("AGPL-3.0-or-later")]
    public void Evaluate_DeprecatedStrongCopyleftIdentifiers_AreForbiddenNotMerelyUnclassified(
        string licence)
    {
        LicenseViolation finding = Assert.Single(
            new LicensePolicy().Evaluate([Dep(licence)], isCorePackage: false));

        Assert.Contains("forbidden", finding.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unclassified", finding.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("LGPL-2.1")]
    [InlineData("LGPL-3.0")]
    [InlineData("LGPL-3.0-or-later")]
    public void Evaluate_DeprecatedWeakCopyleftIdentifiers_TrackTheirCurrentForm(string licence)
    {
        var policy = new LicensePolicy();

        Assert.Single(policy.Evaluate([Dep(licence)], isCorePackage: true));
        Assert.Empty(policy.Evaluate([Dep(licence)], isCorePackage: false));
    }

    [Fact]
    public void Evaluate_WeakCopyleft_FailsInCoreButPassesInAnOptionalPackage()
    {
        // ADR-009: the core ships licence-clean; restricted dependencies go
        // in packages a consumer opts into.
        var policy = new LicensePolicy();

        Assert.Single(policy.Evaluate([Dep("MPL-2.0")], isCorePackage: true));
        Assert.Empty(policy.Evaluate([Dep("MPL-2.0")], isCorePackage: false));
    }

    [Fact]
    public void Evaluate_UndeclaredLicence_FailsClosed()
    {
        // An unclassified licence is one nobody has read. Failing closed
        // costs a five-minute classification; failing open costs a licence
        // review after release.
        IReadOnlyList<LicenseViolation> violations =
            new LicensePolicy().Evaluate([Dep(null!)], isCorePackage: false);

        Assert.Equal(LicenseDisposition.Unknown, Assert.Single(violations).Disposition);
    }

    [Fact]
    public void Evaluate_UnrecognisedLicence_FailsClosed()
    {
        Assert.Single(new LicensePolicy().Evaluate([Dep("Weird-Custom-1.0")], isCorePackage: false));
    }

    [Fact]
    public void Violation_Message_NamesTheTransitivePath()
    {
        // Nobody adds strong copyleft deliberately; it arrives four levels
        // down a chain somebody added for a date formatter. The report has to
        // say so, or the reader looks for it in the project file and does not
        // find it.
        LicenseViolation violation = new LicensePolicy()
            .Evaluate([Dep("GPL-3.0-only", transitive: true)], isCorePackage: false)[0];

        Assert.Contains("[transitive]", violation.ToString(), StringComparison.Ordinal);
    }
}

/// <summary>
/// Phase 34: "a breaking change without a major-version bump fails the build".
/// </summary>
public sealed class ApiCompatibilityGateTests
{
    private static readonly string[] Baseline =
    [
        "#nullable enable",
        "Edpf.Thing",
        "Edpf.Thing.Name.get -> string!",
        "Edpf.Thing.Save(int id) -> void",
    ];

    [Fact]
    public void Compare_NoChange_RequiresOnlyAPatch()
    {
        ApiDiff diff = ApiCompatibilityGate.Compare(Baseline, Baseline);

        Assert.True(diff.IsEmpty);
        Assert.Equal(RequiredVersionBump.Patch, diff.RequiredBump);
    }

    [Fact]
    public void Compare_AdditionOnly_RequiresMinor()
    {
        string[] current = [.. Baseline, "Edpf.Thing.Delete() -> void"];

        ApiDiff diff = ApiCompatibilityGate.Compare(Baseline, current);

        Assert.Equal(RequiredVersionBump.Minor, diff.RequiredBump);
        Assert.Single(diff.Added);
    }

    [Fact]
    public void Compare_Removal_RequiresMajor()
    {
        string[] current = Baseline.Where(e => !e.Contains("Save", StringComparison.Ordinal)).ToArray();

        ApiDiff diff = ApiCompatibilityGate.Compare(Baseline, current);

        Assert.Equal(RequiredVersionBump.Major, diff.RequiredBump);
    }

    [Fact]
    public void Compare_SignatureChange_IsTreatedAsARemoval()
    {
        // int -> long removes one entry and adds another. That is correct: a
        // consumer compiled against the old signature does not care that
        // something similarly named still exists.
        string[] current =
        [
            "#nullable enable",
            "Edpf.Thing",
            "Edpf.Thing.Name.get -> string!",
            "Edpf.Thing.Save(long id) -> void",
        ];

        ApiDiff diff = ApiCompatibilityGate.Compare(Baseline, current);

        Assert.Equal(RequiredVersionBump.Major, diff.RequiredBump);
        Assert.Single(diff.Removed);
        Assert.Single(diff.Added);
    }

    [Fact]
    public void Compare_Always_IgnoresTheNullableDirective()
    {
        ApiDiff diff = ApiCompatibilityGate.Compare(
            ["#nullable enable", "Edpf.Thing"],
            ["Edpf.Thing"]);

        Assert.True(diff.IsEmpty);
    }

    [Theory]
    [InlineData(RequiredVersionBump.Major, true)]
    [InlineData(RequiredVersionBump.Minor, false)]
    [InlineData(RequiredVersionBump.Patch, false)]
    public void IsSufficient_BreakingChange_AcceptsOnlyMajor(
        RequiredVersionBump proposed, bool expected)
    {
        string[] current = Baseline.Where(e => !e.Contains("Save", StringComparison.Ordinal)).ToArray();
        ApiDiff diff = ApiCompatibilityGate.Compare(Baseline, current);

        Assert.Equal(expected, ApiCompatibilityGate.IsSufficient(diff, proposed));
    }

    [Fact]
    public void IsSufficient_LargerBumpThanRequired_IsAllowed()
    {
        // Over-signalling a change is never harmful; under-signalling breaks
        // consumers who pinned by SemVer.
        ApiDiff diff = ApiCompatibilityGate.Compare(Baseline, [.. Baseline, "Edpf.Thing.New() -> void"]);

        Assert.True(ApiCompatibilityGate.IsSufficient(diff, RequiredVersionBump.Major));
    }
}
