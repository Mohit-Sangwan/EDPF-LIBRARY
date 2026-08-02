namespace Edpf.ArchitectureTests;

/// <summary>
/// Phase 08c's sandbox, enforced structurally rather than by review.
/// </summary>
/// <remarks>
/// <para>
/// The formula engine evaluates text a customer typed. Its safety argument is
/// not that the evaluator is careful — it is that **the capabilities do not
/// exist in the assembly.** A function cannot perform I/O if nothing in
/// <c>Edpf.Formula</c> can open a file; it cannot reach reflection if nothing
/// there can load a type.
/// </para>
/// <para>
/// That argument decays the moment someone adds a convenience. "Just let
/// LOOKUP read the reference table from disk" is a reasonable-sounding request
/// that ends the sandbox, and this test is what turns it into a conversation.
/// </para>
/// </remarks>
public sealed class FormulaSandboxBoundaryTests
{
    /// <summary>
    /// Capabilities that must not appear in the formula assembly. Each is a
    /// route out of the sandbox, and none has a legitimate use in evaluating
    /// an arithmetic expression.
    /// </summary>
    private static readonly (string Token, string Why)[] Forbidden =
    [
        ("System.IO", "file and stream access"),
        ("System.Net", "network access"),
        ("System.Reflection", "type and member discovery"),
        ("System.Diagnostics.Process", "process launch"),
        ("Activator.", "arbitrary type instantiation"),
        ("AppDomain", "assembly loading"),
        ("Assembly.", "assembly loading"),
        ("Type.GetType", "type resolution from a string"),
        ("Emit", "runtime code generation"),
        ("CodeDom", "runtime code generation"),
        ("dynamic ", "late binding"),
        ("Environment.", "ambient machine state"),
        ("Random", "non-determinism, which would make a formula untestable"),
        ("DateTime.Now", "ambient time, which would make a formula untestable"),
        ("DateTime.UtcNow", "ambient time, which would make a formula untestable"),
        ("DateTimeOffset.UtcNow", "ambient time, which would make a formula untestable"),
    ];

    [Fact]
    public void FormulaAssembly_ContainsNoEscapeCapability()
    {
        var violations = new List<string>();
        int scanned = 0;

        foreach (string file in RepoRoot.SourceFiles("src"))
        {
            if (!file.Contains("Edpf.Formula", StringComparison.Ordinal))
            {
                continue;
            }

            scanned++;

            foreach ((string line, int number) in File.ReadLines(file).Select((l, i) => (l, i + 1)))
            {
                string trimmed = line.TrimStart();

                // Comments name these capabilities to explain why they are
                // absent. Deleting that explanation to satisfy a grep would
                // lose the reasoning that makes the rule maintainable.
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("///", StringComparison.Ordinal)
                    || trimmed.StartsWith('*'))
                {
                    continue;
                }

                foreach ((string token, string why) in Forbidden)
                {
                    if (line.Contains(token, StringComparison.Ordinal))
                    {
                        violations.Add(
                            $"{Path.GetFileName(file)}:{number} uses '{token}' ({why}) — {trimmed}");
                    }
                }
            }
        }

        // A scan that silently matched nothing would pass for the wrong
        // reason, and would keep passing after someone moved or renamed the
        // assembly this test exists to guard.
        Assert.True(scanned > 0, "No formula source files were scanned; the path filter is wrong.");

        Assert.True(
            violations.Count == 0,
            "The formula engine evaluates text a customer typed. Its safety rests on these capabilities "
            + "being absent from the assembly, not on the evaluator being careful with them:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void FormulaAssembly_TakesNoPackageDependency()
    {
        // Every added package is added attack surface inside the sandbox, and
        // a transitive dependency could reintroduce exactly what the test
        // above forbids.
        string project = Path.Combine(
            RepoRoot.Locate(), "src", "Edpf.Formula", "Edpf.Formula.csproj");

        Assert.DoesNotContain(
            "PackageReference", File.ReadAllText(project), StringComparison.Ordinal);
    }
}
