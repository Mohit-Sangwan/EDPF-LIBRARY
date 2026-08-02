using System.Text.RegularExpressions;

namespace Edpf.ArchitectureTests;

/// <summary>
/// ADR-026's revisit trigger, turned into a build failure: *"a second
/// expression evaluator appears anywhere in the codebase."*
/// </summary>
/// <remarks>
/// <para>
/// Phase 17c's rules platform is the first real test of that decision. Its
/// conditions and outcomes are user-authored expressions, and writing a small
/// purpose-built evaluator for them would have been the path of least
/// resistance — decision-table conditions look simpler than general formulas,
/// right up until someone needs a function call in one.
/// </para>
/// <para>
/// **Two evaluators means two sandboxes, and the second is always the weaker.**
/// It gets written under deadline, by someone who has not read the threat
/// model, for a case that "obviously" does not need one. So the rules engine
/// consumes <c>Edpf.Formula</c>, and this test is what keeps the next one
/// doing the same.
/// </para>
/// </remarks>
public sealed partial class SingleEvaluatorTests
{
    /// <summary>
    /// Types that turn source *text* into a syntax tree. A compiler over an
    /// existing object model — <c>QueryCompiler</c> turning a
    /// <c>Specification</c> into SQL — is a different thing and is not caught
    /// here, because it never parses caller text in the first place.
    /// </summary>
    [GeneratedRegex(@"\b(class|record|struct)\s+\w*(Parser|Lexer|Tokenizer|Interpreter|ScriptEngine)\b")]
    private static partial Regex ExpressionFrontEnd();

    [Fact]
    public void OnlyTheFormulaAssemblyParsesUserAuthoredExpressions()
    {
        var violations = new List<string>();
        int scanned = 0;

        foreach (string file in RepoRoot.SourceFiles("src"))
        {
            scanned++;

            if (file.Contains("Edpf.Formula", StringComparison.Ordinal))
            {
                continue;
            }

            foreach ((string line, int number) in File.ReadLines(file).Select((l, i) => (l, i + 1)))
            {
                string trimmed = line.TrimStart();

                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("///", StringComparison.Ordinal)
                    || trimmed.StartsWith('*'))
                {
                    continue;
                }

                if (ExpressionFrontEnd().IsMatch(line))
                {
                    violations.Add($"{Path.GetFileName(file)}:{number} — {trimmed}");
                }
            }
        }

        Assert.True(scanned > 0, "No source files were scanned; the path filter is wrong.");

        Assert.True(
            violations.Count == 0,
            "A second expression front end has appeared. Two evaluators means two sandboxes, and the "
            + "second is always the weaker one — it gets written under deadline for a case that "
            + "'obviously' does not need one. Build on Edpf.Formula instead (ADR-026):"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void RulesPlatform_ConsumesTheFormulaEngine_RatherThanItsOwn()
    {
        // The positive half: not merely "no second parser", but that the rules
        // platform actually routes through the first one.
        string project = Path.Combine(RepoRoot.Locate(), "src", "Edpf.Rules", "Edpf.Rules.csproj");

        Assert.Contains("Edpf.Formula", File.ReadAllText(project), StringComparison.Ordinal);
    }
}
