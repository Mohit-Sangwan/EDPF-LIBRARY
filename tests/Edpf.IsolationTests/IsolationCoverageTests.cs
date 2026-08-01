using System.Reflection;

namespace Edpf.IsolationTests;

/// <summary>
/// Makes "the isolation suite covers every route" checkable rather than
/// asserted. Phase 12 §⑦ requires every later phase that adds a data path to
/// extend this suite; without a coverage check, that requirement decays into
/// a line in a document nobody re-reads.
/// </summary>
public sealed class IsolationCoverageTests
{
    private static Dictionary<string, List<string>> CoverageByRoute()
    {
        var coverage = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (string route in IsolationRoutes.All)
        {
            coverage[route] = [];
        }

        foreach (Type type in typeof(IsolationCoverageTests).Assembly.GetTypes())
        {
            foreach (CoversIsolationRouteAttribute attribute in
                     type.GetCustomAttributes<CoversIsolationRouteAttribute>())
            {
                if (coverage.TryGetValue(attribute.Route, out List<string>? classes))
                {
                    classes.Add(type.Name);
                }
            }
        }

        return coverage;
    }

    [Fact]
    public void EveryEnumeratedRoute_HasAtLeastOneCoveringTestClass()
    {
        Dictionary<string, List<string>> coverage = CoverageByRoute();

        string[] uncovered = coverage
            .Where(entry => entry.Value.Count == 0)
            .Select(entry => entry.Key)
            .ToArray();

        Assert.True(
            uncovered.Length == 0,
            "Isolation routes with no covering test class: " + string.Join(", ", uncovered)
            + ". Every route in IsolationRoutes.All must have a class marked "
            + "[CoversIsolationRoute]; a route without one is an untested boundary.");
    }

    [Fact]
    public void RouteList_MatchesPhase12_TwelveRoutes()
    {
        // Phase 12 §④ enumerates twelve. Adding a route is expected as later
        // phases add data paths; silently *removing* one is not.
        Assert.True(
            IsolationRoutes.All.Count >= 12,
            $"The suite enumerates {IsolationRoutes.All.Count} routes; Phase 12 §④ requires at least 12.");

        Assert.Equal(IsolationRoutes.All.Count, IsolationRoutes.All.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryCoveringAttribute_NamesAKnownRoute()
    {
        // Guards against a typo silently creating an uncovered route.
        var known = new HashSet<string>(IsolationRoutes.All, StringComparer.Ordinal);

        IEnumerable<string> unknown = typeof(IsolationCoverageTests).Assembly
            .GetTypes()
            .SelectMany(t => t.GetCustomAttributes<CoversIsolationRouteAttribute>())
            .Select(a => a.Route)
            .Where(route => !known.Contains(route));

        Assert.Empty(unknown);
    }
}
