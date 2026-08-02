using System;
using Edpf.Core.Guards;

namespace Edpf.Formula;

/// <summary>
/// The resource ceilings a formula is evaluated under (Phase 08c).
/// </summary>
/// <remarks>
/// <para>
/// A user-authored formula is untrusted input regardless of who authored it.
/// Even without I/O or reflection, an unbounded expression is a denial of
/// service: <c>POWER(9,9)</c> nested a few times, or a
/// <c>REPT</c>-style concatenation, exhausts memory on the server that
/// evaluates it.
/// </para>
/// <para>
/// **The primary control is a deterministic step budget, not a wall-clock
/// timeout.** A wall-clock limit makes the same formula pass on an idle
/// machine and fail on a loaded one — which means it cannot be tested, and a
/// limit that cannot be tested is a limit nobody can rely on. The step budget
/// gives the same verdict every time. A wall-clock ceiling is also available
/// as defence in depth for pathological cases the step budget prices wrongly.
/// </para>
/// </remarks>
public sealed class FormulaLimits
{
    /// <summary>The default ceilings.</summary>
    public static FormulaLimits Default { get; } = new();

    /// <summary>Initializes ceilings.</summary>
    /// <param name="maxDepth">Maximum nesting depth of the parsed expression.</param>
    /// <param name="maxNodes">Maximum node count in the parsed expression.</param>
    /// <param name="maxSteps">Maximum evaluation steps.</param>
    /// <param name="maxTextLength">Maximum length of any text value produced.</param>
    /// <param name="maxSourceLength">Maximum length of the formula source.</param>
    /// <param name="wallClockCeiling">Optional wall-clock ceiling, checked periodically.</param>
    /// <exception cref="ArgumentOutOfRangeException">A ceiling is not positive.</exception>
    public FormulaLimits(
        int maxDepth = 32,
        int maxNodes = 2_000,
        int maxSteps = 100_000,
        int maxTextLength = 32_768,
        int maxSourceLength = 8_192,
        TimeSpan? wallClockCeiling = null)
    {
        MaxDepth = Guard.Positive(maxDepth, nameof(maxDepth));
        MaxNodes = Guard.Positive(maxNodes, nameof(maxNodes));
        MaxSteps = Guard.Positive(maxSteps, nameof(maxSteps));
        MaxTextLength = Guard.Positive(maxTextLength, nameof(maxTextLength));
        MaxSourceLength = Guard.Positive(maxSourceLength, nameof(maxSourceLength));
        WallClockCeiling = wallClockCeiling;
    }

    /// <summary>
    /// Maximum nesting depth. Caps recursion in the parser and the evaluator,
    /// both of which descend the tree — a deep enough expression would
    /// otherwise overflow the stack, which is a crash rather than an error.
    /// </summary>
    public int MaxDepth { get; }

    /// <summary>Maximum node count, which bounds the parse itself.</summary>
    public int MaxNodes { get; }

    /// <summary>Maximum evaluation steps.</summary>
    public int MaxSteps { get; }

    /// <summary>
    /// Maximum length of any text value produced. Concatenation doubles
    /// cheaply; ten doublings from a 1 KB string is 1 MB, twenty is 1 GB.
    /// </summary>
    public int MaxTextLength { get; }

    /// <summary>Maximum length of the formula source.</summary>
    public int MaxSourceLength { get; }

    /// <summary>
    /// Optional wall-clock ceiling. Defence in depth only — the step budget is
    /// the control that is tested, because it is the one that behaves the same
    /// on every machine.
    /// </summary>
    public TimeSpan? WallClockCeiling { get; }
}
