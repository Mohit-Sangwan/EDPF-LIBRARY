using Edpf.Abstractions.Primitives;
using Edpf.DataPlatform.Classification;

namespace Edpf.Cli.Commands;

/// <summary>
/// <c>edpf classify-schema</c> — scans a CSV sample for unclassified PII/PHI
/// (Phase 33, driving the Phase 23 classifier).
/// </summary>
/// <remarks>
/// The intended use is a CI step over a sample extract from a lower
/// environment: it catches the column somebody added without a
/// <c>[DataClassification]</c> attribute, which is the column that silently
/// opts out of encryption, redaction, audit and export control.
/// </remarks>
public static class ClassifySchemaCommand
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args.Any(a => a is "-h" or "--help"))
        {
            Console.WriteLine("""
                Usage: edpf classify-schema <sample.csv> [--declared <declared.csv>]

                Scans a CSV sample and reports fields whose values look like PII or
                PHI. With --declared (a two-column file of field,classification),
                reports only fields classified below what their content suggests —
                classification drift.

                Exit code 1 if any high-confidence drift is found, so this can gate
                a merge.

                Classification values: Public, Internal, Confidential, Pii, Phi, Pci
                """);
            return args.Length == 0 ? 1 : 0;
        }

        string samplePath = args[0];
        if (!File.Exists(samplePath))
        {
            Console.Error.WriteLine($"Sample file not found: {samplePath}");
            return 1;
        }

        Dictionary<string, IReadOnlyList<string>> samples = ReadSamples(samplePath);
        if (samples.Count == 0)
        {
            Console.Error.WriteLine("Sample file has no data rows.");
            return 1;
        }

        string? declaredPath = ReadOption(args, "--declared");
        Dictionary<string, DataClassificationLevel> declared =
            declaredPath is null ? [] : ReadDeclared(declaredPath);

        IReadOnlyList<ClassificationFinding> drift = DataClassifier.DetectDrift(samples, declared);

        Console.WriteLine($"edpf classify-schema — {Path.GetFileName(samplePath)}");
        Console.WriteLine($"  {samples.Count} field(s) sampled, {declared.Count} declared classification(s).");
        Console.WriteLine();

        if (drift.Count == 0)
        {
            Console.WriteLine("No classification drift detected.");
            return 0;
        }

        int blocking = 0;
        foreach (ClassificationFinding finding in drift)
        {
            // Check-digit-confirmed findings block; pattern-only findings are
            // reported for review. A classifier that cries wolf gets muted.
            string severity = finding.IsHighConfidence ? "DRIFT " : "review";
            Console.WriteLine(
                $"  {severity}  {finding.FieldName}: looks like {finding.DetectedKind}, "
                + $"should be classified {finding.SuggestedLevel}");

            if (finding.IsHighConfidence)
            {
                blocking++;
            }
        }

        Console.WriteLine();
        Console.WriteLine(blocking > 0
            ? $"{blocking} high-confidence finding(s). Classify these fields before merging."
            : $"{drift.Count} finding(s) for review; none check-digit confirmed.");

        // Findings name fields and kinds, never values — this output lands in
        // CI logs, which are themselves a disclosure surface.
        return blocking > 0 ? 1 : 0;
    }

    private static Dictionary<string, IReadOnlyList<string>> ReadSamples(string path)
    {
        string[] lines = File.ReadAllLines(path);
        var samples = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        if (lines.Length < 2)
        {
            return samples;
        }

        string[] headers = SplitCsv(lines[0]);
        var columns = new List<string>[headers.Length];
        for (int i = 0; i < headers.Length; i++)
        {
            columns[i] = [];
        }

        foreach (string line in lines.Skip(1))
        {
            string[] cells = SplitCsv(line);
            for (int i = 0; i < headers.Length && i < cells.Length; i++)
            {
                columns[i].Add(cells[i]);
            }
        }

        for (int i = 0; i < headers.Length; i++)
        {
            samples[headers[i]] = columns[i];
        }

        return samples;
    }

    private static Dictionary<string, DataClassificationLevel> ReadDeclared(string path)
    {
        var declared = new Dictionary<string, DataClassificationLevel>(StringComparer.Ordinal);

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Declared-classification file not found: {path}");
            return declared;
        }

        foreach (string line in File.ReadAllLines(path))
        {
            string[] cells = SplitCsv(line);
            if (cells.Length >= 2
                && Enum.TryParse(cells[1], ignoreCase: true, out DataClassificationLevel level))
            {
                declared[cells[0]] = level;
            }
        }

        return declared;
    }

    private static string[] SplitCsv(string line)
        => line.Split(',').Select(cell => cell.Trim().Trim('"')).ToArray();

    private static string? ReadOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
