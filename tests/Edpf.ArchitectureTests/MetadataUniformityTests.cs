namespace Edpf.ArchitectureTests;

/// <summary>
/// Phase 05b's structural invariant: protection must never depend on where a
/// field's definition came from.
/// </summary>
/// <remarks>
/// <para>
/// The metadata platform's whole security argument is that compile-time and
/// runtime-defined fields travel one code path. The moment something branches
/// on <c>IsRuntimeDefined</c> to decide whether to encrypt, redact, audit or
/// export, there are two paths — and the runtime one, carrying fields nobody
/// reviewed at compile time, is the one that will be weaker.
/// </para>
/// <para>
/// The branch would look reasonable when written. "Custom fields don't need
/// the audit overhead" is a sentence someone will say, in a hurry, with a
/// deadline. This test is what makes them argue for it in a pull request
/// instead.
/// </para>
/// </remarks>
public sealed class MetadataUniformityTests
{
    /// <summary>
    /// The property exists for diagnostics, migration tooling and storage
    /// planning. These files are where reading it is legitimate.
    /// </summary>
    private static readonly string[] LegitimateReaders =
    [
        // Declares the property.
        "IEntityMetadata.cs",

        // Implements it.
        "FieldMetadata.cs",

        // Sets it while scanning compiled types.
        "CompiledEntityScanner.cs",

        // Enforces that overlay fields declare themselves runtime-defined.
        "MetadataRepository.cs",
    ];

    [Fact]
    public void NoProtectionDecision_BranchesOnWhetherAFieldWasRuntimeDefined()
    {
        var violations = new List<string>();

        foreach (string file in RepoRoot.SourceFiles("src"))
        {
            string name = Path.GetFileName(file);
            if (LegitimateReaders.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            foreach ((string line, int number) in File.ReadLines(file).Select((l, i) => (l, i + 1)))
            {
                string trimmed = line.TrimStart();

                // Prose may discuss the property — the surrounding comments
                // explain precisely why this rule exists, and deleting that
                // explanation to satisfy a grep would be the wrong trade.
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("///", StringComparison.Ordinal)
                    || trimmed.StartsWith('*'))
                {
                    continue;
                }

                if (line.Contains("IsRuntimeDefined", StringComparison.Ordinal))
                {
                    violations.Add($"{name}:{number} — {trimmed}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "A file outside the metadata platform reads IsRuntimeDefined. Protection follows Classification "
            + "alone; branching on the definition's origin creates a second, weaker path for exactly the "
            + "fields nobody reviewed at compile time (Phase 05b, Appendix I.0):"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void ProtectionRequirements_AreDerivedInExactlyOnePlace()
    {
        // Encryption, redaction, audit and subject-access export must all ask
        // the same table. If any of them built its own answer from a
        // classification level, the two would drift, and the gap between them
        // would be a disclosure that no single subsystem's tests would catch.
        var deciders = new List<string>();

        foreach (string file in RepoRoot.SourceFiles("src"))
        {
            string name = Path.GetFileName(file);
            if (string.Equals(name, "ProtectionPolicy.cs", StringComparison.Ordinal)
                || string.Equals(name, "DataProtection.cs", StringComparison.Ordinal))
            {
                continue;
            }

            string content = File.ReadAllText(file);

            // A switch over classification levels outside the policy is the
            // shape a second opinion takes.
            if (content.Contains("DataClassificationLevel.Phi =>", StringComparison.Ordinal)
                || content.Contains("case DataClassificationLevel.Phi", StringComparison.Ordinal))
            {
                deciders.Add(name);
            }
        }

        Assert.True(
            deciders.Count == 0,
            "A file outside ProtectionPolicy maps classification levels to handling. There must be one "
            + "table, or the subsystems consulting it will drift apart:"
            + Environment.NewLine + string.Join(Environment.NewLine, deciders));
    }
}
