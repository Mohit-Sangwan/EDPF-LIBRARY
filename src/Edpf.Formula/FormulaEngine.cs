using System;
using System.Collections.Generic;
using Edpf.Abstractions.Metadata;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;
using Edpf.Core.Guards;

namespace Edpf.Formula;

/// <summary>
/// A named, effective-dated formula definition (Phase 08c).
/// </summary>
/// <remarks>
/// Effective dating for the same reason metadata carries it (ADR-025): a
/// invoice raised in 2024 must be reproducible from the tax rule that applied
/// in 2024, not from the one in force when someone reopens it.
/// </remarks>
public sealed class FormulaDefinition
{
    /// <summary>Initializes a definition.</summary>
    /// <param name="name">The name other formulas reference it by.</param>
    /// <param name="source">The formula text.</param>
    /// <param name="effectiveFrom">When it takes effect, inclusive.</param>
    /// <param name="effectiveTo">When it ceases to apply, exclusive; open-ended if null.</param>
    /// <exception cref="ArgumentException">The effective range is inverted.</exception>
    public FormulaDefinition(
        string name,
        string source,
        DateTimeOffset effectiveFrom,
        DateTimeOffset? effectiveTo = null)
    {
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name));
        Source = Guard.NotNull(source, nameof(source));
        EffectiveFrom = effectiveFrom;
        EffectiveTo = effectiveTo;

        if (effectiveTo.HasValue && effectiveTo.Value <= effectiveFrom)
        {
            throw new ArgumentException(
                "A definition's effective-to must follow its effective-from.", nameof(effectiveTo));
        }
    }

    /// <summary>The name other formulas reference it by.</summary>
    public string Name { get; }

    /// <summary>The formula text.</summary>
    public string Source { get; }

    /// <summary>When it takes effect, inclusive.</summary>
    public DateTimeOffset EffectiveFrom { get; }

    /// <summary>When it ceases to apply, exclusive.</summary>
    public DateTimeOffset? EffectiveTo { get; }

    /// <summary>
    /// True when this definition applies at <paramref name="asOf"/>.
    /// </summary>
    /// <param name="asOf">The instant to test.</param>
    /// <returns>Whether it is in effect.</returns>
    public bool AppliesAt(DateTimeOffset asOf)
        => asOf >= EffectiveFrom && (!EffectiveTo.HasValue || asOf < EffectiveTo.Value);
}

/// <summary>
/// Parses, validates and evaluates formulas (Phase 08c).
/// </summary>
/// <remarks>
/// <para>
/// The entry point an application uses. Beyond parse and evaluate it provides
/// the two things that make a formula safe to put in a customer's hands: a
/// **dependency graph with circular-reference detection**, and a **dry-run
/// harness** so a formula can be unit-tested before it goes live.
/// </para>
/// <para>
/// A circular reference is not a stack overflow to be caught — it is a
/// definition error to be reported before anything is stored, naming the cycle
/// so the author can see what they wrote.
/// </para>
/// </remarks>
public sealed class FormulaEngine
{
    private readonly FormulaParser _parser;
    private readonly FormulaEvaluator _evaluator;
    private readonly List<FormulaDefinition> _definitions = [];

    /// <summary>Initializes an engine.</summary>
    /// <param name="limits">Resource ceilings; defaults applied when omitted.</param>
    /// <param name="functions">The function registry; the standard library when omitted.</param>
    public FormulaEngine(FormulaLimits? limits = null, IFormulaFunctionRegistry? functions = null)
    {
        FormulaLimits effective = limits ?? FormulaLimits.Default;
        _parser = new FormulaParser(functions, effective);
        _evaluator = new FormulaEvaluator(effective);
    }

    /// <summary>
    /// Parses <paramref name="source"/> without evaluating it.
    /// </summary>
    /// <param name="source">The formula text.</param>
    /// <returns>The parsed expression, or a failure.</returns>
    public Result<FormulaNode> Parse(string source) => _parser.Parse(source);

    /// <summary>
    /// Parses and evaluates <paramref name="source"/> in one call.
    /// </summary>
    /// <param name="source">The formula text.</param>
    /// <param name="context">The field value source.</param>
    /// <returns>The value, or a failure.</returns>
    public Result<FormulaValue> Evaluate(string source, IFormulaContext context)
    {
        Result<FormulaNode> parsed = _parser.Parse(source);
        return parsed.IsFailure
            ? Result.Failure<FormulaValue>(parsed.Error!)
            : _evaluator.Evaluate(parsed.Value, context);
    }

    /// <summary>
    /// Evaluates an already-parsed expression.
    /// </summary>
    /// <param name="expression">The parsed formula.</param>
    /// <param name="context">The field value source.</param>
    /// <returns>The value, or a failure.</returns>
    public Result<FormulaValue> Evaluate(FormulaNode expression, IFormulaContext context)
        => _evaluator.Evaluate(expression, context);

    /// <summary>
    /// Registers a definition.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <returns>Success, or a failure if it does not parse or overlaps an existing one.</returns>
    public Result Register(FormulaDefinition definition)
    {
        Guard.NotNull(definition, nameof(definition));

        Result<FormulaNode> parsed = _parser.Parse(definition.Source);
        if (parsed.IsFailure)
        {
            // Refused at registration, not discovered at evaluation. A formula
            // that fails to parse when an invoice run reaches it has already
            // stopped the invoice run.
            return Result.Failure(parsed.Error!);
        }

        foreach (FormulaDefinition existing in _definitions)
        {
            if (!string.Equals(existing.Name, definition.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DateTimeOffset existingEnd = existing.EffectiveTo ?? DateTimeOffset.MaxValue;
            DateTimeOffset newEnd = definition.EffectiveTo ?? DateTimeOffset.MaxValue;

            if (existing.EffectiveFrom < newEnd && definition.EffectiveFrom < existingEnd)
            {
                return Result.Failure(new Error(
                    ErrorCodes.Duplicate,
                    $"'{definition.Name}' is already defined over an overlapping effective period. Close "
                    + "the earlier definition before opening a new one.",
                    ErrorCategory.Conflict));
            }
        }

        _definitions.Add(definition);
        return Result.Success();
    }

    /// <summary>
    /// Finds a definition in effect at a point in time.
    /// </summary>
    /// <param name="name">The definition name.</param>
    /// <param name="asOf">The instant.</param>
    /// <returns>The definition, or a failure.</returns>
    public Result<FormulaDefinition> Resolve(string name, DateTimeOffset asOf)
    {
        foreach (FormulaDefinition definition in _definitions)
        {
            if (string.Equals(definition.Name, name, StringComparison.OrdinalIgnoreCase)
                && definition.AppliesAt(asOf))
            {
                return Result.Success(definition);
            }
        }

        return Result.Failure<FormulaDefinition>(new Error(
            ErrorCodes.NotFound, $"No formula named '{name}' is in effect.", ErrorCategory.NotFound));
    }

    /// <summary>
    /// Returns the field names an expression reads.
    /// </summary>
    /// <param name="expression">The parsed formula.</param>
    /// <returns>The distinct field names, in source order of first appearance.</returns>
    /// <remarks>
    /// This is what a dependency graph is built from, and what an
    /// authorization check runs over before a formula is allowed to execute.
    /// </remarks>
    public static IReadOnlyList<string> ReferencedFields(FormulaNode expression)
    {
        Guard.NotNull(expression, nameof(expression));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();

        Collect(expression, seen, ordered);
        return ordered;
    }

    private static void Collect(FormulaNode node, HashSet<string> seen, List<string> ordered)
    {
        switch (node)
        {
            case FieldReferenceNode reference:
                if (seen.Add(reference.FieldName))
                {
                    ordered.Add(reference.FieldName);
                }

                break;

            case UnaryNode unary:
                Collect(unary.Operand, seen, ordered);
                break;

            case BinaryNode binary:
                Collect(binary.Left, seen, ordered);
                Collect(binary.Right, seen, ordered);
                break;

            case FunctionCallNode call:
                foreach (FormulaNode argument in call.Arguments)
                {
                    Collect(argument, seen, ordered);
                }

                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Determines the classification a formula's result will carry, without
    /// evaluating it.
    /// </summary>
    /// <param name="expression">The parsed formula.</param>
    /// <param name="metadata">The metadata field references resolve against.</param>
    /// <returns>The highest classification among the fields read.</returns>
    /// <remarks>
    /// Lets a caller decide where a computed value may be *stored* before
    /// computing it. A KPI derived from PHI needs a PHI-classified home; the
    /// answer must be knowable at design time, not discovered when the value
    /// has already been written somewhere unprotected.
    /// </remarks>
    public static Result<DataClassificationLevel> ResultClassification(
        FormulaNode expression, IEntityMetadata metadata)
    {
        Guard.NotNull(metadata, nameof(metadata));

        DataClassificationLevel level = DataClassificationLevel.Public;

        foreach (string fieldName in ReferencedFields(expression))
        {
            Result<IFieldMetadata> resolved = metadata.ResolveField(fieldName);
            if (resolved.IsFailure)
            {
                return Result.Failure<DataClassificationLevel>(resolved.Error!);
            }

            level = FormulaValue.Combine(level, resolved.Value.Classification);
        }

        return Result.Success(level);
    }
}
