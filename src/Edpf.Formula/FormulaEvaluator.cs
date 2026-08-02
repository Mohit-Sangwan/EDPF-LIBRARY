using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Edpf.Abstractions.Metadata;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;
using Edpf.Core.Guards;

namespace Edpf.Formula;

/// <summary>
/// Supplies field values to a formula (Phase 08c).
/// </summary>
/// <remarks>
/// Field references resolve through Phase 05b metadata (ADR-025), so a formula
/// can name a runtime-defined custom field and gets the same authorization and
/// classification treatment as a compiled one.
/// </remarks>
public interface IFormulaContext
{
    /// <summary>The metadata the formula's field references resolve against.</summary>
    IEntityMetadata Metadata { get; }

    /// <summary>
    /// Reads a field's value.
    /// </summary>
    /// <param name="field">The resolved field.</param>
    /// <returns>The value, blank when unset.</returns>
    FormulaValue Read(IFieldMetadata field);
}

/// <summary>
/// Evaluates a parsed formula under a resource budget (Phase 08c).
/// </summary>
/// <remarks>
/// <para>
/// **Two properties hold for every evaluation, and both are tested.**
/// </para>
/// <para>
/// *Arithmetic is decimal.* The master document is explicit that *"a rounding
/// error in a dosage or an invoice is not a cosmetic defect"*. Binary floating
/// point cannot represent 0.1, so <c>0.1 + 0.2</c> is not <c>0.3</c>; decimal
/// is base-10 and it is.
/// </para>
/// <para>
/// *Classification propagates.* A result is never less classified than
/// anything it was computed from. Without this, a formula is a laundering
/// mechanism — read a PHI field, multiply by one, and emit an answer no
/// redactor, encryptor or export filter will touch.
/// </para>
/// </remarks>
public sealed class FormulaEvaluator
{
    private readonly FormulaLimits _limits;

    /// <summary>Initializes an evaluator.</summary>
    /// <param name="limits">Resource ceilings; defaults applied when omitted.</param>
    public FormulaEvaluator(FormulaLimits? limits = null) => _limits = limits ?? FormulaLimits.Default;

    /// <summary>
    /// Evaluates <paramref name="expression"/> against <paramref name="context"/>.
    /// </summary>
    /// <param name="expression">The parsed formula.</param>
    /// <param name="context">The field value source.</param>
    /// <returns>The value, or a failure.</returns>
    public Result<FormulaValue> Evaluate(FormulaNode expression, IFormulaContext context)
    {
        Guard.NotNull(expression, nameof(expression));
        Guard.NotNull(context, nameof(context));

        var state = new EvaluationState(_limits);
        return Evaluate(expression, context, state, depth: 0);
    }

    private Result<FormulaValue> Evaluate(
        FormulaNode node, IFormulaContext context, EvaluationState state, int depth)
    {
        Result<FormulaValue> budget = state.Step(depth);
        if (budget.IsFailure)
        {
            return budget;
        }

        switch (node)
        {
            case LiteralNode literal:
                return Result.Success(literal.Value);

            case FieldReferenceNode reference:
                return ReadField(reference, context);

            case UnaryNode unary:
                return EvaluateUnary(unary, context, state, depth);

            case BinaryNode binary:
                return EvaluateBinary(binary, context, state, depth);

            case FunctionCallNode call:
                return EvaluateCall(call, context, state, depth);

            default:
                // Unreachable while the hierarchy stays closed — and it is
                // closed by a private protected constructor, not by hope.
                return Failure($"Unsupported expression node '{node.GetType().Name}'.");
        }
    }

    private static Result<FormulaValue> ReadField(FieldReferenceNode reference, IFormulaContext context)
    {
        Result<IFieldMetadata> resolved = context.Metadata.ResolveField(reference.FieldName);
        if (resolved.IsFailure)
        {
            return Result.Failure<FormulaValue>(resolved.Error!);
        }

        IFieldMetadata field = resolved.Value;

        // The read carries the field's classification into the expression, and
        // nothing downstream can lower it.
        return Result.Success(context.Read(field).WithClassificationAtLeast(field.Classification));
    }

    private Result<FormulaValue> EvaluateUnary(
        UnaryNode unary, IFormulaContext context, EvaluationState state, int depth)
    {
        Result<FormulaValue> operand = Evaluate(unary.Operand, context, state, depth + 1);
        if (operand.IsFailure)
        {
            return operand;
        }

        if (unary.IsNegation)
        {
            return operand.Value.Kind != FormulaValueKind.Number
                ? Failure("Negation requires a number.")
                : Result.Success(FormulaValue.FromNumber(
                    -operand.Value.Number, operand.Value.Classification));
        }

        return operand.Value.Kind != FormulaValueKind.Boolean
            ? Failure("NOT requires a boolean.")
            : Result.Success(FormulaValue.FromBoolean(
                !operand.Value.Boolean, operand.Value.Classification));
    }

    private Result<FormulaValue> EvaluateBinary(
        BinaryNode binary, IFormulaContext context, EvaluationState state, int depth)
    {
        Result<FormulaValue> left = Evaluate(binary.Left, context, state, depth + 1);
        if (left.IsFailure)
        {
            return left;
        }

        Result<FormulaValue> right = Evaluate(binary.Right, context, state, depth + 1);
        if (right.IsFailure)
        {
            return right;
        }

        DataClassificationLevel classification =
            FormulaValue.Combine(left.Value.Classification, right.Value.Classification);

        switch (binary.Operator)
        {
            case FormulaOperator.Concat:
            {
                string combined = ToText(left.Value) + ToText(right.Value);
                return combined.Length > _limits.MaxTextLength
                    ? LimitFailure($"A text result exceeds the maximum length of {_limits.MaxTextLength}.")
                    : Result.Success(FormulaValue.FromText(combined, classification));
            }

            case FormulaOperator.Equal:
                return Result.Success(FormulaValue.FromBoolean(
                    AreEqual(left.Value, right.Value), classification));

            case FormulaOperator.NotEqual:
                return Result.Success(FormulaValue.FromBoolean(
                    !AreEqual(left.Value, right.Value), classification));

            case FormulaOperator.LessThan:
            case FormulaOperator.LessThanOrEqual:
            case FormulaOperator.GreaterThan:
            case FormulaOperator.GreaterThanOrEqual:
                return Compare(binary.Operator, left.Value, right.Value, classification);

            default:
                return Arithmetic(binary.Operator, left.Value, right.Value, classification);
        }
    }

    private static Result<FormulaValue> Arithmetic(
        FormulaOperator op, FormulaValue left, FormulaValue right, DataClassificationLevel classification)
    {
        if (left.Kind != FormulaValueKind.Number || right.Kind != FormulaValueKind.Number)
        {
            return Failure("Arithmetic requires numbers.");
        }

        try
        {
            switch (op)
            {
                case FormulaOperator.Add:
                    return Result.Success(FormulaValue.FromNumber(left.Number + right.Number, classification));

                case FormulaOperator.Subtract:
                    return Result.Success(FormulaValue.FromNumber(left.Number - right.Number, classification));

                case FormulaOperator.Multiply:
                    return Result.Success(FormulaValue.FromNumber(left.Number * right.Number, classification));

                case FormulaOperator.Divide:
                    // Returned as a failure rather than an infinity or a NaN.
                    // A dosage calculation that quietly yields infinity is
                    // worse than one that refuses to answer.
                    return right.Number == 0m
                        ? Failure("Division by zero.")
                        : Result.Success(FormulaValue.FromNumber(left.Number / right.Number, classification));

                case FormulaOperator.Power:
                    return Power(left.Number, right.Number, classification);

                default:
                    return Failure($"Unsupported operator '{op}'.");
            }
        }
        catch (OverflowException)
        {
            // decimal overflows rather than saturating, which is the correct
            // behaviour to surface: a silently clamped invoice total is a
            // wrong number presented as a right one.
            return LimitFailure("The calculation overflowed the decimal range.");
        }
        catch (DivideByZeroException)
        {
            return Failure("Division by zero.");
        }
    }

    internal static Result<FormulaValue> Power(
        decimal baseValue, decimal exponent, DataClassificationLevel classification)
    {
        // Integral exponents only, computed by repeated multiplication in
        // decimal. Math.Pow would take the value through double and hand back
        // a rounding error in a dosage calculation — the exact defect the
        // decimal requirement exists to prevent.
        if (exponent != Math.Truncate(exponent))
        {
            return Failure("POWER supports whole-number exponents only, to keep the result exact in decimal.");
        }

        if (Math.Abs(exponent) > 64m)
        {
            return LimitFailure("POWER supports exponents between -64 and 64.");
        }

        int power = (int)Math.Abs(exponent);
        decimal result = 1m;

        try
        {
            for (int i = 0; i < power; i++)
            {
                result *= baseValue;
            }

            if (exponent < 0m)
            {
                if (result == 0m)
                {
                    return Failure("Division by zero.");
                }

                result = 1m / result;
            }
        }
        catch (OverflowException)
        {
            return LimitFailure("The calculation overflowed the decimal range.");
        }

        return Result.Success(FormulaValue.FromNumber(result, classification));
    }

    private static Result<FormulaValue> Compare(
        FormulaOperator op, FormulaValue left, FormulaValue right, DataClassificationLevel classification)
    {
        int comparison;

        if (left.Kind == FormulaValueKind.Number && right.Kind == FormulaValueKind.Number)
        {
            comparison = decimal.Compare(left.Number, right.Number);
        }
        else if (left.Kind == FormulaValueKind.Timestamp && right.Kind == FormulaValueKind.Timestamp)
        {
            comparison = DateTimeOffset.Compare(left.Timestamp, right.Timestamp);
        }
        else if (left.Kind == FormulaValueKind.Text && right.Kind == FormulaValueKind.Text)
        {
            // Ordinal: a formula's verdict must not depend on the server's
            // culture (Phase 27). Culture-aware ordering would make the same
            // billing rule decide differently in two regions.
            comparison = string.CompareOrdinal(left.Text, right.Text);
        }
        else
        {
            return Failure("Comparison requires two values of the same kind.");
        }

        bool value = op switch
        {
            FormulaOperator.LessThan => comparison < 0,
            FormulaOperator.LessThanOrEqual => comparison <= 0,
            FormulaOperator.GreaterThan => comparison > 0,
            _ => comparison >= 0,
        };

        return Result.Success(FormulaValue.FromBoolean(value, classification));
    }

    private Result<FormulaValue> EvaluateCall(
        FunctionCallNode call, IFormulaContext context, EvaluationState state, int depth)
    {
        // IF evaluates lazily: the untaken branch must not run, so that
        // IF([Divisor]=0, 0, [Total]/[Divisor]) is the guard an author expects
        // rather than a division-by-zero either way.
        if (string.Equals(call.Name, "IF", StringComparison.OrdinalIgnoreCase))
        {
            Result<FormulaValue> condition = Evaluate(call.Arguments[0], context, state, depth + 1);
            if (condition.IsFailure)
            {
                return condition;
            }

            if (condition.Value.Kind != FormulaValueKind.Boolean)
            {
                return Failure("IF requires a boolean condition.");
            }

            FormulaNode branch = condition.Value.Boolean ? call.Arguments[1] : call.Arguments[2];
            Result<FormulaValue> taken = Evaluate(branch, context, state, depth + 1);

            // The condition's classification still counts: which branch was
            // taken is itself information derived from the condition.
            return taken.IsFailure
                ? taken
                : Result.Success(taken.Value.WithClassificationAtLeast(condition.Value.Classification));
        }

        var arguments = new List<FormulaValue>(call.Arguments.Count);
        DataClassificationLevel classification = DataClassificationLevel.Public;

        foreach (FormulaNode argumentNode in call.Arguments)
        {
            Result<FormulaValue> argument = Evaluate(argumentNode, context, state, depth + 1);
            if (argument.IsFailure)
            {
                return argument;
            }

            arguments.Add(argument.Value);
            classification = FormulaValue.Combine(classification, argument.Value.Classification);
        }

        Result<FormulaValue> result = FormulaLibrary.Invoke(call.Name, arguments, _limits);
        return result.IsFailure
            ? result
            : Result.Success(result.Value.WithClassificationAtLeast(classification));
    }

    internal static string ToText(FormulaValue value) => value.Kind switch
    {
        FormulaValueKind.Text => value.Text ?? string.Empty,
        FormulaValueKind.Number => value.Number.ToString(CultureInfo.InvariantCulture),
        FormulaValueKind.Boolean => value.Boolean ? "TRUE" : "FALSE",
        FormulaValueKind.Timestamp => value.Timestamp.ToString("O", CultureInfo.InvariantCulture),
        _ => string.Empty,
    };

    private static bool AreEqual(FormulaValue left, FormulaValue right)
    {
        if (left.Kind != right.Kind)
        {
            return false;
        }

        return left.Kind switch
        {
            FormulaValueKind.Number => left.Number == right.Number,
            FormulaValueKind.Text => string.Equals(left.Text, right.Text, StringComparison.Ordinal),
            FormulaValueKind.Boolean => left.Boolean == right.Boolean,
            FormulaValueKind.Timestamp => left.Timestamp == right.Timestamp,
            _ => true,
        };
    }

    internal static Result<FormulaValue> Failure(string message)
        => Result.Failure<FormulaValue>(new Error(
            ErrorCodes.ValidationFailed, message, ErrorCategory.Validation));

    internal static Result<FormulaValue> LimitFailure(string message)
        => Result.Failure<FormulaValue>(new Error(
            ErrorCodes.QueryCostExceeded, message, ErrorCategory.Validation));

    private sealed class EvaluationState
    {
        private readonly FormulaLimits _limits;
        private readonly Stopwatch? _clock;
        private int _steps;

        public EvaluationState(FormulaLimits limits)
        {
            _limits = limits;
            _clock = limits.WallClockCeiling.HasValue ? Stopwatch.StartNew() : null;
        }

        public Result<FormulaValue> Step(int depth)
        {
            if (depth > _limits.MaxDepth)
            {
                return LimitFailure($"Evaluation nests deeper than the maximum of {_limits.MaxDepth}.");
            }

            if (++_steps > _limits.MaxSteps)
            {
                return LimitFailure($"Evaluation exceeded the budget of {_limits.MaxSteps} steps.");
            }

            // Checked every 256 steps: often enough to stop a pathological
            // expression, rarely enough that the check is not itself the cost.
            if (_clock is not null && (_steps & 0xFF) == 0 && _clock.Elapsed > _limits.WallClockCeiling!.Value)
            {
                return LimitFailure("Evaluation exceeded its time ceiling.");
            }

            return Result.Success(FormulaValue.Blank);
        }
    }
}
