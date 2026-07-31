namespace Edpf.ArchitectureTests;

/// <summary>Locates the repository root (marked by Edpf.slnx) for source scans.</summary>
internal static class RepoRoot
{
    internal static string Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Edpf.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("Repository root (Edpf.slnx) not found above " + AppContext.BaseDirectory);
    }

    internal static IEnumerable<string> SourceFiles(string relativeRoot)
    {
        string root = Path.Combine(Locate(), relativeRoot);
        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }
}
