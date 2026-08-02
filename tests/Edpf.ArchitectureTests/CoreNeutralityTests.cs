using System.Text.RegularExpressions;

namespace Edpf.ArchitectureTests;

/// <summary>
/// ADR-024's mitigation: an architecture test forbidding healthcare
/// terminology in core assemblies (Phase 24b §⑧).
/// </summary>
/// <remarks>
/// <para>
/// The originating specification put <c>SavePatient()</c>, <c>SaveEncounter()</c>
/// and <c>SaveLabOrder()</c> directly on the framework. That is a layering
/// violation — a framework serving ERP, banking and telecom cannot expose
/// <c>SavePatient()</c> — and the correction was to move that content into an
/// optional vertical package.
/// </para>
/// <para>
/// A correction like that decays without enforcement. The pressure is real
/// and constant: healthcare is the design-partner domain, so the quickest fix
/// for any clinical requirement is always to reach into the core. This test
/// is what makes the quickest fix fail.
/// </para>
/// </remarks>
public sealed partial class CoreNeutralityTests
{
    /// <summary>
    /// Domain terms that must not appear in a domain-neutral core. Word
    /// boundaries throughout, so "Patient" is caught while "impatient" and
    /// "Order" inside "OrderBy" are not.
    /// </summary>
    [GeneratedRegex(
        @"\b(Patient|Encounter|Diagnosis|Prescription|Clinician|Specimen|Immunization|"
        + @"MedicalRecord|LabOrder|Radiology|Ward|Admission|Discharge)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ClinicalTerms();

    /// <summary>
    /// Assemblies that must stay domain-neutral. The whole commercial premise
    /// — one framework across healthcare, ERP, banking and government — rests
    /// on this list staying clean.
    /// </summary>
    private static readonly string[] CoreAssemblies =
    [
        "Edpf.Abstractions",
        "Edpf.Core",
        "Edpf.Compatibility",
        "Edpf.Diagnostics",
        "Edpf.Configuration",
        "Edpf.Data",
        "Edpf.Caching",
        "Edpf.Security",
        "Edpf.Globalization",
        "Edpf.Operations",
        "Edpf.Extensions.DependencyInjection",

        // Added as the platform grew. Every one of these was written after the
        // rule existed and had to be kept neutral deliberately — Edpf.Devices
        // most of all, since a device platform for a hospital is where clinical
        // vocabulary wants to leak in hardest. Its plausibility bands live in
        // verticals/Edpf.Healthcare.Domain for exactly that reason.
        "Edpf.Metadata",
        "Edpf.Formula",
        "Edpf.Rules",
        "Edpf.Barcode",
        "Edpf.DataQuality",
        "Edpf.Devices",
    ];

    [Fact]
    public void CoreAssemblies_ContainNoClinicalTerminology()
    {
        var violations = new List<string>();

        foreach (string file in RepoRoot.SourceFiles("src"))
        {
            if (!CoreAssemblies.Any(a => file.Contains(a, StringComparison.Ordinal)))
            {
                continue;
            }

            foreach ((string line, int number) in File.ReadLines(file).Select((l, i) => (l, i + 1)))
            {
                string trimmed = line.TrimStart();

                // Comments and documentation may cite clinical examples —
                // "a medication administration time" explains *why* a rule
                // exists, and losing that explanation would cost more than
                // the purity gains.
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("///", StringComparison.Ordinal)
                    || trimmed.StartsWith('*'))
                {
                    continue;
                }

                if (ClinicalTerms().IsMatch(line))
                {
                    violations.Add(
                        $"{Path.GetFileName(file)}:{number} — {trimmed}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Clinical terminology found in a domain-neutral core assembly (ADR-024). "
            + "Move it to verticals/Edpf.Healthcare.*, or generalise the concept:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void CoreAssemblies_DoNotReferenceAnyVerticalPackage()
    {
        // The dependency must point one way only. A core assembly referencing
        // a vertical would make the vertical mandatory, which is the same
        // layering violation wearing a different hat.
        var violations = new List<string>();

        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot.Locate(), "src"), "*.csproj", SearchOption.AllDirectories))
        {
            string content = File.ReadAllText(file);
            if (content.Contains("verticals", StringComparison.OrdinalIgnoreCase)
                || content.Contains("Edpf.Healthcare", StringComparison.Ordinal)
                || content.Contains("Edpf.Finance", StringComparison.Ordinal))
            {
                violations.Add(Path.GetFileName(file));
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void VerticalPackage_BuildsOnThePublicSurfaceAlone()
    {
        // Phase 24b's extension-model test: the vertical must not reach past
        // the public API. InternalsVisibleTo would let it, and would mean the
        // core's extension points are inadequate — ADR-024 says fix the
        // extension point, never special-case the vertical.
        var violations = new List<string>();

        foreach (string file in RepoRoot.SourceFiles("src"))
        {
            string content = File.ReadAllText(file);
            if (content.Contains("InternalsVisibleTo", StringComparison.Ordinal)
                && content.Contains("Healthcare", StringComparison.Ordinal))
            {
                violations.Add(Path.GetFileName(file));
            }
        }

        Assert.Empty(violations);
    }
}
