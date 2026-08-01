using System.Globalization;

namespace Edpf.Cli.Commands;

/// <summary>
/// <c>edpf doctor</c> — checks for the conditions that break EDPF at runtime
/// rather than at build time (Phase 33).
/// </summary>
/// <remarks>
/// The checks are chosen from failures that are cheap to detect and expensive
/// to diagnose in production: a missing signing key that only surfaces on the
/// first authenticated request, an ICU-less runtime that silently breaks
/// collation, and a machine whose clock has drifted far enough to invalidate
/// tokens.
/// </remarks>
public static class DoctorCommand
{
    public static int Run(string[] args)
    {
        if (args.Any(a => a is "-h" or "--help"))
        {
            Console.WriteLine("""
                Usage: edpf doctor [--path <dir>]

                Checks the environment and repository for conditions that break
                EDPF at runtime. Exit code 1 if any check fails.
                """);
            return 0;
        }

        string root = ReadOption(args, "--path") ?? Directory.GetCurrentDirectory();

        var results = new List<CheckResult>
        {
            CheckGlobalization(),
            CheckClockSkew(),
            CheckTimeZoneData(),
            CheckProductionSecrets(root),
            CheckPublicApiBaselines(root),
        };

        Console.WriteLine($"edpf doctor — {root}");
        Console.WriteLine();

        foreach (CheckResult result in results)
        {
            string marker = result.Passed ? "  ok  " : "  FAIL";
            Console.WriteLine($"{marker}  {result.Name}");
            Console.WriteLine($"        {result.Detail}");
        }

        int failures = results.Count(r => !r.Passed);
        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"All {results.Count} checks passed."
            : $"{failures} of {results.Count} checks failed.");

        return failures == 0 ? 0 : 1;
    }

    private static CheckResult CheckGlobalization()
    {
        // Invariant globalization mode silently breaks per-language collation
        // and culture-aware formatting — a patient list sorts wrongly and
        // nothing errors (Phase 27).
        bool invariant = AppContext.TryGetSwitch(
            "System.Globalization.Invariant", out bool enabled) && enabled;

        return new CheckResult(
            "ICU globalization available",
            !invariant,
            invariant
                ? "Invariant globalization is ON. Collation and culture formatting will be wrong, silently."
                : $"Culture data present (current: {CultureInfo.CurrentCulture.Name}).");
    }

    private static CheckResult CheckClockSkew()
    {
        // A machine clock far from real time invalidates JWTs and corrupts
        // audit ordering. The check is coarse — it only catches an obviously
        // unset clock, which is the case that actually occurs in containers.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool plausible = now.Year is >= 2024 and <= 2100;

        return new CheckResult(
            "System clock plausible",
            plausible,
            plausible
                ? $"UTC now is {now:u}."
                : $"UTC now is {now:u}, which is implausible. Tokens and audit ordering will misbehave.");
    }

    private static CheckResult CheckTimeZoneData()
    {
        // Slim container images routinely omit tzdata, and every IANA lookup
        // then fails at runtime (Phase 27).
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
            return new CheckResult("IANA time-zone data present", true, "IANA identifiers resolve.");
        }
        catch (TimeZoneNotFoundException)
        {
            return new CheckResult(
                "IANA time-zone data present",
                false,
                "IANA identifiers do not resolve. Install tzdata; local-time conversion will fail at runtime.");
        }
    }

    private static CheckResult CheckProductionSecrets(string root)
    {
        // A signing key committed to appsettings is the failure that a secret
        // scanner catches after the commit is already in history.
        var offenders = new List<string>();

        foreach (string file in SafeEnumerate(root, "appsettings*.json"))
        {
            string content = File.ReadAllText(file);
            if (content.Contains("SigningKeyBase64\":", StringComparison.OrdinalIgnoreCase)
                && !content.Contains("SigningKeyBase64\": \"\"", StringComparison.OrdinalIgnoreCase))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        return new CheckResult(
            "No signing keys in configuration files",
            offenders.Count == 0,
            offenders.Count == 0
                ? "No committed signing keys found."
                : $"Signing key present in: {string.Join(", ", offenders)}. Move it to ISecretStore.");
    }

    private static CheckResult CheckPublicApiBaselines(string root)
    {
        // A tracked project whose baseline went missing stops failing on API
        // changes, and nobody notices until a breaking change ships.
        string[] baselines = SafeEnumerate(root, "PublicAPI.Unshipped.txt").ToArray();

        return new CheckResult(
            "Public API baselines present",
            baselines.Length > 0,
            baselines.Length > 0
                ? $"{baselines.Length} baseline file(s) tracked."
                : "No PublicAPI.Unshipped.txt found. API change control is not active.");
    }

    private static IEnumerable<string> SafeEnumerate(string root, string pattern)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            return Directory
                .EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                                StringComparison.Ordinal)
                         && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                                StringComparison.Ordinal));
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? ReadOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private sealed record CheckResult(string Name, bool Passed, string Detail);
}
