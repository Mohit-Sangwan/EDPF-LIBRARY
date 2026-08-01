using Edpf.Cli.Commands;

namespace Edpf.Cli;

/// <summary>
/// The <c>edpf</c> CLI (Phase 33).
/// </summary>
/// <remarks>
/// Hand-rolled argument parsing rather than a parser library: the command
/// surface is small and stable, and a tool that ships to consumers should
/// carry the fewest dependencies it can (ADR-009 — every dependency is a
/// licence and a CVE surface for everyone who installs it).
/// </remarks>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        string[] rest = args.Skip(1).ToArray();

        return args[0] switch
        {
            "doctor" => DoctorCommand.Run(rest),
            "classify-schema" => ClassifySchemaCommand.Run(rest),
            "check-licenses" => CheckLicensesCommand.Run(rest),
            "check-api" => CheckApiCommand.Run(rest),
            "version" => PrintVersion(),
            _ => Unknown(args[0]),
        };
    }

    private static bool IsHelp(string arg)
        => arg is "-h" or "--help" or "help" or "-?";

    private static int PrintVersion()
    {
        Console.WriteLine($"edpf {ThisAssembly.Version}");
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        Console.Error.WriteLine("Run 'edpf --help' for the command list.");
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine($"""
            edpf {ThisAssembly.Version} — Enterprise Data Platform Framework tooling

            Usage: edpf <command> [options]

            Commands:
              doctor                    Check the environment and repository for
                                        conditions that break EDPF at runtime.
              classify-schema <file>    Scan a CSV sample for unclassified PII/PHI
                                        and report classification drift.
              check-licenses <file>     Evaluate a dependency licence list against
                                        the policy gate (ADR-009).
              check-api <old> <new>     Diff two PublicAPI baselines and report the
                                        SemVer bump the change requires.
              version                   Print the tool version.

            Run 'edpf <command> --help' for command detail.
            """);
    }
}

/// <summary>Build-stamped identity, so a bug report can name an exact build.</summary>
internal static class ThisAssembly
{
    internal static string Version =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
