using System.Text.RegularExpressions;

namespace Edpf.ArchitectureTests;

/// <summary>
/// Z.4 analyzer rules enforced by source scan until the dedicated Roslyn
/// analyzer package ships (Phase 33 tooling). Same severity: a hit is a
/// failing build.
/// </summary>
public sealed partial class SourceRuleTests
{
    [GeneratedRegex(@"^\s*#if", RegexOptions.Multiline)]
    private static partial Regex ConditionalCompilation();

    [GeneratedRegex(@"\bDateTime(Offset)?\.(UtcNow|Now|Today)\b")]
    private static partial Regex SystemTimeRead();

    [GeneratedRegex(@"\bnew\s+Random\b|\bRandom\.Shared\b")]
    private static partial Regex InsecureRandom();

    /// <summary>
    /// Matches a rule only against code lines — XML docs and comments may
    /// legitimately reference the banned API by name.
    /// </summary>
    private static bool CodeMatches(string file, Regex rule)
        => File.ReadLines(file)
            .Select(line => line.TrimStart())
            .Where(line => !line.StartsWith("//", StringComparison.Ordinal))
            .Any(rule.IsMatch);

    /// <summary>Rule EDPF0002: no `#if` outside Edpf.Compatibility.</summary>
    [Fact]
    public void SrcSources_OutsideCompatibility_ContainNoConditionalCompilation()
    {
        IEnumerable<string> violations = RepoRoot.SourceFiles("src")
            .Where(f => !f.Contains("Edpf.Compatibility", StringComparison.Ordinal))
            .Where(f => ConditionalCompilation().IsMatch(File.ReadAllText(f)));

        Assert.Empty(violations);
    }

    /// <summary>
    /// Rule EDPF0003: no direct system-time reads anywhere in src/ or
    /// samples/ except the sanctioned polyfill boundary — time flows through
    /// IClock (Z.3 rule 4).
    /// </summary>
    [Fact]
    public void SrcAndSampleSources_OutsideCompatibility_ContainNoSystemTimeReads()
    {
        IEnumerable<string> violations = RepoRoot.SourceFiles("src")
            .Concat(RepoRoot.SourceFiles("samples"))
            .Where(f => !f.Contains("Edpf.Compatibility", StringComparison.Ordinal))
            .Where(f => CodeMatches(f, SystemTimeRead()));

        Assert.Empty(violations);
    }

    /// <summary>
    /// Rule EDPF0004: System.Random never appears in security code — only
    /// RandomNumberGenerator (Z.3 rule 5). Scoped to all src/ and the
    /// skeleton's security namespace.
    /// </summary>
    [Fact]
    public void SecuritySources_UseNoInsecureRandom()
    {
        IEnumerable<string> violations = RepoRoot.SourceFiles("src")
            .Concat(RepoRoot.SourceFiles("samples").Where(f => f.Contains("Security", StringComparison.Ordinal)))
            .Where(f => CodeMatches(f, InsecureRandom()));

        Assert.Empty(violations);
    }

    [GeneratedRegex(@"^\s*using\s+System\.Security\.Cryptography")]
    private static partial Regex CryptographyUsing();

    /// <summary>
    /// Z.10: crypto flows through <c>ICryptoProvider</c>. Every assembly
    /// except the sanctioned implementation home must reach cryptography
    /// through that seam rather than importing
    /// <c>System.Security.Cryptography</c> directly.
    /// </summary>
    /// <remarks>
    /// <c>Edpf.Security</c> is the one exemption, because it *is* the
    /// implementation — the rule exists so that crypto lives in one reviewed,
    /// audited place, not so that it lives nowhere. Adding a second exemption
    /// should be a deliberate architectural decision, which is why the list is
    /// explicit here rather than a pattern.
    /// </remarks>
    [Fact]
    public void NonSecurityAssemblies_DoNotTouchCryptographyDirectly()
    {
        string[] sanctionedCryptoAssemblies = ["Edpf.Security"];

        IEnumerable<string> violations = RepoRoot.SourceFiles("src")
            .Where(f => !sanctionedCryptoAssemblies.Any(
                assembly => f.Contains(assembly, StringComparison.Ordinal)))
            .Where(f => CodeMatches(f, CryptographyUsing()));

        Assert.Empty(violations);
    }
}
