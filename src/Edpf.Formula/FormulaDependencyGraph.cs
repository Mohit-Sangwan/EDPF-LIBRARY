using System;
using System.Collections.Generic;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Formula;

/// <summary>
/// Orders computed fields by dependency, and refuses cycles (Phase 08c).
/// </summary>
/// <remarks>
/// <para>
/// Computed fields reference each other: <c>Total</c> depends on
/// <c>Subtotal</c> and <c>Tax</c>, and <c>Tax</c> depends on
/// <c>Subtotal</c>. Something has to decide what to evaluate first, and that
/// something has to notice when an author has written a cycle.
/// </para>
/// <para>
/// **A cycle is reported as a definition error naming the cycle, not caught as
/// a stack overflow.** A stack overflow cannot be caught in .NET — it takes
/// the process down — so detecting the cycle before evaluation is not a
/// nicety, it is the difference between an error message and an outage.
/// </para>
/// </remarks>
public sealed class FormulaDependencyGraph
{
    private readonly Dictionary<string, List<string>> _dependencies =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> _order = [];

    /// <summary>
    /// Declares that <paramref name="name"/> is computed from
    /// <paramref name="dependsOn"/>.
    /// </summary>
    /// <param name="name">The computed field.</param>
    /// <param name="dependsOn">The names it reads.</param>
    /// <exception cref="ArgumentException">The field is already declared.</exception>
    public void Add(string name, IEnumerable<string> dependsOn)
    {
        Guard.NotNullOrWhiteSpace(name, nameof(name));
        Guard.NotNull(dependsOn, nameof(dependsOn));

        if (_dependencies.ContainsKey(name))
        {
            throw new ArgumentException(
                $"'{name}' is already declared. A silent replacement would change what an existing "
                + "calculation depends on without anyone seeing it.",
                nameof(name));
        }

        _dependencies[name] = [.. dependsOn];
        _order.Add(name);
    }

    /// <summary>
    /// Returns an evaluation order, or the cycle that prevents one.
    /// </summary>
    /// <returns>
    /// The names in dependency order — every field appearing after everything
    /// it reads — or a failure naming the cycle.
    /// </returns>
    public Result<IReadOnlyList<string>> Resolve()
    {
        var state = new Dictionary<string, Mark>(StringComparer.OrdinalIgnoreCase);
        var sorted = new List<string>(_dependencies.Count);
        var path = new List<string>();

        // Iterating the insertion order rather than the dictionary's keeps the
        // output stable across runs, which matters because an unstable order
        // makes a failing calculation reproduce only sometimes.
        foreach (string name in _order)
        {
            Result<IReadOnlyList<string>> visit = Visit(name, state, sorted, path);
            if (visit.IsFailure)
            {
                return visit;
            }
        }

        return Result.Success<IReadOnlyList<string>>(sorted);
    }

    private Result<IReadOnlyList<string>> Visit(
        string name, Dictionary<string, Mark> state, List<string> sorted, List<string> path)
    {
        if (state.TryGetValue(name, out Mark mark))
        {
            if (mark == Mark.Done)
            {
                return Result.Success<IReadOnlyList<string>>(sorted);
            }

            // Still being visited: we have arrived back where we started.
            int start = path.FindIndex(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
            string cycle = string.Join(" → ", path.GetRange(start, path.Count - start)) + " → " + name;

            return Result.Failure<IReadOnlyList<string>>(new Error(
                ErrorCodes.ValidationFailed,
                $"The formulas form a circular reference: {cycle}. Each value would depend on itself, so "
                + "there is no order in which they can be computed.",
                ErrorCategory.Validation));
        }

        state[name] = Mark.Visiting;
        path.Add(name);

        if (_dependencies.TryGetValue(name, out List<string>? dependencies))
        {
            foreach (string dependency in dependencies)
            {
                Result<IReadOnlyList<string>> visit = Visit(dependency, state, sorted, path);
                if (visit.IsFailure)
                {
                    return visit;
                }
            }
        }

        path.RemoveAt(path.Count - 1);
        state[name] = Mark.Done;

        // Only declared computed fields go into the order. A dependency on a
        // plain stored field is satisfied already and needs no evaluation slot.
        if (_dependencies.ContainsKey(name))
        {
            sorted.Add(name);
        }

        return Result.Success<IReadOnlyList<string>>(sorted);
    }

    private enum Mark
    {
        Visiting = 0,
        Done = 1,
    }
}
