using System.Reflection;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Time;
using NetArchTest.Rules;

namespace Edpf.ArchitectureTests;

/// <summary>
/// Phase 01 §⑤: Clean Architecture enforced as executable rules.
/// </summary>
public sealed class LayeringTests
{
    private static readonly Assembly Abstractions = typeof(Result).Assembly;
    private static readonly Assembly Core = typeof(SystemClock).Assembly;

    /// <summary>
    /// Rule EDPF0001: Edpf.Abstractions depends on nothing but the BCL.
    /// </summary>
    [Fact]
    public void Abstractions_ReferencedAssemblies_AreBclOnly()
    {
        string[] allowedPrefixes = ["System", "mscorlib", "netstandard", "Microsoft.CSharp"];

        IEnumerable<string> violations = Abstractions
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => !allowedPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)));

        Assert.Empty(violations);
    }

    [Fact]
    public void Core_DoesNotReference_ProvidersOrEfCore()
    {
        string[] forbidden =
        [
            "Microsoft.EntityFrameworkCore", "Npgsql", "Microsoft.Data.SqlClient", "Dapper",
        ];

        IEnumerable<string> violations = Core
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => forbidden.Any(f => name.StartsWith(f, StringComparison.Ordinal)));

        Assert.Empty(violations);
    }

    [Fact]
    public void Abstractions_Types_ResideInMirroredNamespaces()
    {
        // Z.2: namespace mirrors folder path — everything in this assembly
        // lives under Edpf.Abstractions.*.
        TestResult result = Types.InAssembly(Abstractions)
            .That().ArePublic()
            .Should().ResideInNamespaceStartingWith("Edpf.Abstractions")
            .GetResult();

        Assert.True(result.IsSuccessful, Format(result));
    }

    [Fact]
    public void Core_PublicClasses_AreSealedOrAbstractOrStatic()
    {
        // Z.3 rule 7: sealed by default; virtual only with a documented reason.
        IEnumerable<string> violations = Core.GetExportedTypes()
            .Where(t => t.IsClass && !t.IsSealed && !t.IsAbstract)
            .Select(t => t.FullName!);

        Assert.Empty(violations);
    }

    [Fact]
    public void SrcAssemblies_AsyncPublicMethods_AcceptCancellationToken()
    {
        // Z.3 rule 3 / rule EDPF0007. Scans every public Task-returning method
        // on the framework assemblies (properties excluded).
        var violations = new List<string>();

        foreach (Type type in Abstractions.GetExportedTypes().Concat(Core.GetExportedTypes()))
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                bool returnsTask = typeof(Task).IsAssignableFrom(method.ReturnType);
                if (!returnsTask || method.IsSpecialName)
                {
                    continue;
                }

                bool hasToken = method.GetParameters().Any(p => p.ParameterType == typeof(CancellationToken));
                if (!hasToken)
                {
                    violations.Add($"{type.FullName}.{method.Name}");
                }
            }
        }

        Assert.Empty(violations);
    }

    private static string Format(TestResult result)
        => "Violations: " + string.Join(", ", result.FailingTypes?.Select(t => t.FullName ?? "?") ?? []);
}
