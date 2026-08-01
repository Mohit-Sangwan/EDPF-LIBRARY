using Edpf.Operations.SupplyChain;

namespace Edpf.Cli.Commands;

/// <summary>
/// <c>edpf check-licenses</c> — the licence-policy gate as a CI step
/// (Phase 34).
/// </summary>
public static class CheckLicensesCommand
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args.Any(a => a is "-h" or "--help"))
        {
            Console.WriteLine("""
                Usage: edpf check-licenses <licenses.csv> [--core]

                Evaluates a dependency licence list against the policy gate.
                CSV columns: package,version,licence,transitive

                --core  Evaluate as the core package graph, where weak-copyleft
                        licences are also violations (ADR-009: the core ships
                        licence-clean; restricted dependencies go in optional
                        packages a consumer opts into).

                Exit code 1 on any violation.
                """);
            return args.Length == 0 ? 1 : 0;
        }

        string path = args[0];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Licence file not found: {path}");
            return 1;
        }

        bool isCore = args.Contains("--core");
        List<DependencyLicense> dependencies = ReadDependencies(path);

        IReadOnlyList<LicenseViolation> violations =
            new LicensePolicy().Evaluate(dependencies, isCore);

        Console.WriteLine($"edpf check-licenses — {Path.GetFileName(path)}");
        Console.WriteLine(
            $"  {dependencies.Count} dependencies evaluated"
            + $"{(isCore ? " as the core package graph" : string.Empty)}.");
        Console.WriteLine();

        if (violations.Count == 0)
        {
            Console.WriteLine("No licence violations.");
            return 0;
        }

        foreach (LicenseViolation violation in violations)
        {
            Console.WriteLine($"  FAIL  {violation}");
        }

        Console.WriteLine();
        Console.WriteLine($"{violations.Count} licence violation(s).");
        return 1;
    }

    private static List<DependencyLicense> ReadDependencies(string path)
    {
        var dependencies = new List<DependencyLicense>();

        foreach (string line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            string[] cells = line.Split(',').Select(c => c.Trim()).ToArray();
            if (cells.Length < 3)
            {
                continue;
            }

            bool transitive = cells.Length > 3
                && bool.TryParse(cells[3], out bool parsed) && parsed;

            dependencies.Add(new DependencyLicense(
                cells[0],
                cells[1],
                string.IsNullOrWhiteSpace(cells[2]) ? null : cells[2],
                transitive));
        }

        return dependencies;
    }
}

/// <summary>
/// <c>edpf check-api</c> — the SemVer compatibility gate (Phase 34).
/// </summary>
public static class CheckApiCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 2 || args.Any(a => a is "-h" or "--help"))
        {
            Console.WriteLine("""
                Usage: edpf check-api <previous-baseline> <current-baseline> [--bump major|minor|patch]

                Diffs two PublicAPI baselines and reports the SemVer bump the change
                requires. With --bump, exits 1 when the proposed bump is insufficient
                for the change — a breaking change under a minor bump breaks every
                consumer who pinned by SemVer.
                """);
            return args.Length < 2 ? 1 : 0;
        }

        if (!File.Exists(args[0]) || !File.Exists(args[1]))
        {
            Console.Error.WriteLine("Both baseline files must exist.");
            return 1;
        }

        ApiDiff diff = ApiCompatibilityGate.Compare(
            File.ReadAllLines(args[0]), File.ReadAllLines(args[1]));

        Console.WriteLine("edpf check-api");
        Console.WriteLine($"  {diff.Added.Count} added, {diff.Removed.Count} removed.");
        Console.WriteLine();

        foreach (string removed in diff.Removed)
        {
            Console.WriteLine($"  REMOVED  {removed}");
        }

        foreach (string added in diff.Added)
        {
            Console.WriteLine($"  added    {added}");
        }

        Console.WriteLine();
        Console.WriteLine($"Required version bump: {diff.RequiredBump}".ToUpperInvariant());

        string? proposed = ReadOption(args, "--bump");
        if (proposed is null)
        {
            return 0;
        }

        if (!Enum.TryParse(proposed, ignoreCase: true, out RequiredVersionBump bump))
        {
            Console.Error.WriteLine($"Unrecognised bump '{proposed}'. Use major, minor or patch.");
            return 1;
        }

        if (ApiCompatibilityGate.IsSufficient(diff, bump))
        {
            Console.WriteLine($"Proposed bump '{bump}' is sufficient.");
            return 0;
        }

        Console.Error.WriteLine(
            $"Proposed bump '{bump}' is INSUFFICIENT; this change requires '{diff.RequiredBump}'.");
        return 1;
    }

    private static string? ReadOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
