using BenchmarkDotNet.Running;

namespace Edpf.Benchmarks;

/// <summary>Benchmark host (Z.9). Publishes measured numbers, never adjectives.</summary>
public static class Program
{
    /// <summary>Runs all benchmarks in the assembly.</summary>
    public static void Main(string[] args)
        => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
