using System.Collections.Generic;
using Edpf.Core.Guards;

namespace Edpf.Formula;

/// <summary>The closed set of operators a formula may use (Phase 08c).</summary>
/// <remarks>
/// An enum rather than an operator string, for the same reason ADR-018 uses
/// one in the query builder: a closed set cannot be extended by a caller, so
/// there is no operator an author can name that the evaluator has not already
/// been written to handle safely.
/// </remarks>
public enum FormulaOperator
{
    /// <summary>Addition.</summary>
    Add = 0,

    /// <summary>Subtraction.</summary>
    Subtract = 1,

    /// <summary>Multiplication.</summary>
    Multiply = 2,

    /// <summary>Division.</summary>
    Divide = 3,

    /// <summary>Exponentiation with an integral exponent.</summary>
    Power = 4,

    /// <summary>Equality.</summary>
    Equal = 5,

    /// <summary>Inequality.</summary>
    NotEqual = 6,

    /// <summary>Less than.</summary>
    LessThan = 7,

    /// <summary>Less than or equal.</summary>
    LessThanOrEqual = 8,

    /// <summary>Greater than.</summary>
    GreaterThan = 9,

    /// <summary>Greater than or equal.</summary>
    GreaterThanOrEqual = 10,

    /// <summary>Text concatenation.</summary>
    Concat = 11,
}

/// <summary>
/// A node in a parsed formula (Phase 08c).
/// </summary>
/// <remarks>
/// <para>
/// **The hierarchy is closed.** The constructor is <c>private protected</c>,
/// so no assembly outside this one can add a node type. That is the
/// "bounded AST" the phase requires, enforced by the compiler rather than by
/// review: there is no node the evaluator can meet that it was not written to
/// handle.
/// </para>
/// <para>
/// Note what has no node: no member access, no indexing, no assignment, no
/// method invocation on a value, no type reference. A formula cannot name a
/// .NET type, so it cannot reach reflection; it cannot call a method, so it
/// cannot reach I/O. The absence is the sandbox.
/// </para>
/// </remarks>
public abstract class FormulaNode
{
    private protected FormulaNode()
    {
    }
}

/// <summary>A constant.</summary>
public sealed class LiteralNode : FormulaNode
{
    /// <summary>Initializes a literal.</summary>
    /// <param name="value">The constant value.</param>
    public LiteralNode(FormulaValue value) => Value = value;

    /// <summary>The constant value.</summary>
    public FormulaValue Value { get; }
}

/// <summary>A reference to a field, resolved through Phase 05b metadata.</summary>
public sealed class FieldReferenceNode : FormulaNode
{
    /// <summary>Initializes a field reference.</summary>
    /// <param name="fieldName">The logical field name.</param>
    public FieldReferenceNode(string fieldName)
        => FieldName = Guard.NotNullOrWhiteSpace(fieldName, nameof(fieldName));

    /// <summary>The logical field name.</summary>
    public string FieldName { get; }
}

/// <summary>Numeric negation or logical NOT.</summary>
public sealed class UnaryNode : FormulaNode
{
    /// <summary>Initializes a unary operation.</summary>
    /// <param name="isNegation">True for arithmetic negation, false for logical NOT.</param>
    /// <param name="operand">The operand.</param>
    public UnaryNode(bool isNegation, FormulaNode operand)
    {
        IsNegation = isNegation;
        Operand = Guard.NotNull(operand, nameof(operand));
    }

    /// <summary>True for arithmetic negation, false for logical NOT.</summary>
    public bool IsNegation { get; }

    /// <summary>The operand.</summary>
    public FormulaNode Operand { get; }
}

/// <summary>A binary operation.</summary>
public sealed class BinaryNode : FormulaNode
{
    /// <summary>Initializes a binary operation.</summary>
    /// <param name="op">The operator.</param>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public BinaryNode(FormulaOperator op, FormulaNode left, FormulaNode right)
    {
        Operator = op;
        Left = Guard.NotNull(left, nameof(left));
        Right = Guard.NotNull(right, nameof(right));
    }

    /// <summary>The operator.</summary>
    public FormulaOperator Operator { get; }

    /// <summary>Left operand.</summary>
    public FormulaNode Left { get; }

    /// <summary>Right operand.</summary>
    public FormulaNode Right { get; }
}

/// <summary>A call to a registered function.</summary>
/// <remarks>
/// The function name resolves against a closed registry. An unknown name is a
/// parse failure, not a runtime lookup — there is no dynamic dispatch path an
/// author could aim at something unintended.
/// </remarks>
public sealed class FunctionCallNode : FormulaNode
{
    /// <summary>Initializes a function call.</summary>
    /// <param name="name">The function name.</param>
    /// <param name="arguments">The arguments.</param>
    public FunctionCallNode(string name, IReadOnlyList<FormulaNode> arguments)
    {
        Name = Guard.NotNullOrWhiteSpace(name, nameof(name));
        Arguments = Guard.NotNull(arguments, nameof(arguments));
    }

    /// <summary>The function name.</summary>
    public string Name { get; }

    /// <summary>The arguments.</summary>
    public IReadOnlyList<FormulaNode> Arguments { get; }
}
