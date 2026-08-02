using System;
using System.Collections.Generic;
using System.Globalization;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.Formula;

/// <summary>
/// Parses formula source into a bounded AST (Phase 08c).
/// </summary>
/// <remarks>
/// <para>
/// Recursive descent, with depth and node ceilings applied **during** the
/// parse rather than after it. Validating a tree that has already been built
/// is too late: building it is what consumed the stack and the memory.
/// </para>
/// <para>
/// Every failure is a <see cref="Result{T}"/>, never an exception. A malformed
/// formula is expected input — someone is typing it — not an exceptional
/// condition, and an author needs to see the position that failed.
/// </para>
/// </remarks>
public sealed class FormulaParser
{
    private readonly FormulaLimits _limits;
    private readonly IFormulaFunctionRegistry _functions;

    private string _source = string.Empty;
    private int _position;
    private int _nodes;

    /// <summary>Initializes a parser.</summary>
    /// <param name="functions">The closed function registry.</param>
    /// <param name="limits">Resource ceilings; defaults applied when omitted.</param>
    public FormulaParser(IFormulaFunctionRegistry? functions = null, FormulaLimits? limits = null)
    {
        _functions = functions ?? FormulaFunctions.Standard;
        _limits = limits ?? FormulaLimits.Default;
    }

    /// <summary>
    /// Parses <paramref name="source"/>.
    /// </summary>
    /// <param name="source">The formula text.</param>
    /// <returns>The parsed expression, or a failure describing where it broke.</returns>
    public Result<FormulaNode> Parse(string source)
    {
        Guard.NotNull(source, nameof(source));

        if (source.Length > _limits.MaxSourceLength)
        {
            return Failure($"The formula exceeds the maximum length of {_limits.MaxSourceLength} characters.");
        }

        _source = source;
        _position = 0;
        _nodes = 0;

        Result<FormulaNode> expression = ParseExpression(depth: 0);
        if (expression.IsFailure)
        {
            return expression;
        }

        SkipWhitespace();
        if (_position < _source.Length)
        {
            return Failure($"Unexpected '{_source[_position]}' at position {_position}.");
        }

        return expression;
    }

    // Comparison binds loosest, then concatenation, then additive,
    // multiplicative, unary, and finally power — which is right-associative,
    // as it is in every spreadsheet an author will have used.
    private Result<FormulaNode> ParseExpression(int depth)
    {
        if (depth > _limits.MaxDepth)
        {
            return Failure($"The formula nests deeper than the maximum of {_limits.MaxDepth}.");
        }

        Result<FormulaNode> left = ParseConcat(depth + 1);
        if (left.IsFailure)
        {
            return left;
        }

        SkipWhitespace();
        FormulaOperator? comparison = TryReadComparison();
        if (comparison is null)
        {
            return left;
        }

        Result<FormulaNode> right = ParseConcat(depth + 1);
        return right.IsFailure ? right : Node(new BinaryNode(comparison.Value, left.Value, right.Value));
    }

    private Result<FormulaNode> ParseConcat(int depth)
    {
        if (depth > _limits.MaxDepth)
        {
            return Failure($"The formula nests deeper than the maximum of {_limits.MaxDepth}.");
        }

        Result<FormulaNode> left = ParseAdditive(depth + 1);
        if (left.IsFailure)
        {
            return left;
        }

        while (true)
        {
            SkipWhitespace();
            if (_position >= _source.Length || _source[_position] != '&')
            {
                return left;
            }

            _position++;
            Result<FormulaNode> right = ParseAdditive(depth + 1);
            if (right.IsFailure)
            {
                return right;
            }

            Result<FormulaNode> combined = Node(
                new BinaryNode(FormulaOperator.Concat, left.Value, right.Value));
            if (combined.IsFailure)
            {
                return combined;
            }

            left = combined;
        }
    }

    private Result<FormulaNode> ParseAdditive(int depth)
    {
        if (depth > _limits.MaxDepth)
        {
            return Failure($"The formula nests deeper than the maximum of {_limits.MaxDepth}.");
        }

        Result<FormulaNode> left = ParseMultiplicative(depth + 1);
        if (left.IsFailure)
        {
            return left;
        }

        while (true)
        {
            SkipWhitespace();
            if (_position >= _source.Length)
            {
                return left;
            }

            char c = _source[_position];
            if (c != '+' && c != '-')
            {
                return left;
            }

            _position++;
            Result<FormulaNode> right = ParseMultiplicative(depth + 1);
            if (right.IsFailure)
            {
                return right;
            }

            Result<FormulaNode> combined = Node(new BinaryNode(
                c == '+' ? FormulaOperator.Add : FormulaOperator.Subtract, left.Value, right.Value));
            if (combined.IsFailure)
            {
                return combined;
            }

            left = combined;
        }
    }

    private Result<FormulaNode> ParseMultiplicative(int depth)
    {
        if (depth > _limits.MaxDepth)
        {
            return Failure($"The formula nests deeper than the maximum of {_limits.MaxDepth}.");
        }

        Result<FormulaNode> left = ParseUnary(depth + 1);
        if (left.IsFailure)
        {
            return left;
        }

        while (true)
        {
            SkipWhitespace();
            if (_position >= _source.Length)
            {
                return left;
            }

            char c = _source[_position];
            if (c != '*' && c != '/')
            {
                return left;
            }

            _position++;
            Result<FormulaNode> right = ParseUnary(depth + 1);
            if (right.IsFailure)
            {
                return right;
            }

            Result<FormulaNode> combined = Node(new BinaryNode(
                c == '*' ? FormulaOperator.Multiply : FormulaOperator.Divide, left.Value, right.Value));
            if (combined.IsFailure)
            {
                return combined;
            }

            left = combined;
        }
    }

    private Result<FormulaNode> ParseUnary(int depth)
    {
        if (depth > _limits.MaxDepth)
        {
            return Failure($"The formula nests deeper than the maximum of {_limits.MaxDepth}.");
        }

        SkipWhitespace();

        if (_position < _source.Length && _source[_position] == '-')
        {
            _position++;
            Result<FormulaNode> operand = ParseUnary(depth + 1);
            return operand.IsFailure ? operand : Node(new UnaryNode(isNegation: true, operand.Value));
        }

        if (_position < _source.Length && _source[_position] == '+')
        {
            _position++;
            return ParseUnary(depth + 1);
        }

        return ParsePower(depth + 1);
    }

    private Result<FormulaNode> ParsePower(int depth)
    {
        if (depth > _limits.MaxDepth)
        {
            return Failure($"The formula nests deeper than the maximum of {_limits.MaxDepth}.");
        }

        Result<FormulaNode> left = ParsePrimary(depth + 1);
        if (left.IsFailure)
        {
            return left;
        }

        SkipWhitespace();
        if (_position >= _source.Length || _source[_position] != '^')
        {
            return left;
        }

        _position++;

        // Right-associative, matching spreadsheet convention.
        Result<FormulaNode> right = ParseUnary(depth + 1);
        return right.IsFailure ? right : Node(new BinaryNode(FormulaOperator.Power, left.Value, right.Value));
    }

    private Result<FormulaNode> ParsePrimary(int depth)
    {
        if (depth > _limits.MaxDepth)
        {
            return Failure($"The formula nests deeper than the maximum of {_limits.MaxDepth}.");
        }

        SkipWhitespace();

        if (_position >= _source.Length)
        {
            return Failure("The formula ends unexpectedly.");
        }

        char c = _source[_position];

        if (c == '(')
        {
            _position++;
            Result<FormulaNode> inner = ParseExpression(depth + 1);
            if (inner.IsFailure)
            {
                return inner;
            }

            SkipWhitespace();
            if (_position >= _source.Length || _source[_position] != ')')
            {
                return Failure($"Expected ')' at position {_position}.");
            }

            _position++;
            return inner;
        }

        if (c == '"')
        {
            return ParseTextLiteral();
        }

        if (char.IsDigit(c) || (c == '.' && _position + 1 < _source.Length && char.IsDigit(_source[_position + 1])))
        {
            return ParseNumberLiteral();
        }

        if (c == '[')
        {
            return ParseBracketedFieldReference();
        }

        if (char.IsLetter(c) || c == '_')
        {
            return ParseIdentifier(depth);
        }

        return Failure($"Unexpected '{c}' at position {_position}.");
    }

    private Result<FormulaNode> ParseTextLiteral()
    {
        int start = ++_position;
        var builder = new System.Text.StringBuilder();

        while (_position < _source.Length)
        {
            char c = _source[_position];

            if (c == '"')
            {
                // A doubled quote is an escaped quote, as in a spreadsheet.
                if (_position + 1 < _source.Length && _source[_position + 1] == '"')
                {
                    builder.Append('"');
                    _position += 2;
                    continue;
                }

                _position++;
                return Node(new LiteralNode(FormulaValue.FromText(builder.ToString())));
            }

            builder.Append(c);
            _position++;

            if (builder.Length > _limits.MaxTextLength)
            {
                return Failure($"A text literal exceeds the maximum length of {_limits.MaxTextLength}.");
            }
        }

        return Failure($"Unterminated text literal starting at position {start - 1}.");
    }

    private Result<FormulaNode> ParseNumberLiteral()
    {
        int start = _position;

        while (_position < _source.Length && (char.IsDigit(_source[_position]) || _source[_position] == '.'))
        {
            _position++;
        }

        string text = _source.Substring(start, _position - start);

        // InvariantCulture, deliberately: a formula stored under one server's
        // culture must evaluate identically under another's, and "1.5" means
        // one and a half everywhere or the engine is not reproducible.
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value))
        {
            return Failure($"'{text}' at position {start} is not a valid number.");
        }

        return Node(new LiteralNode(FormulaValue.FromNumber(value)));
    }

    private Result<FormulaNode> ParseBracketedFieldReference()
    {
        int start = ++_position;

        while (_position < _source.Length && _source[_position] != ']')
        {
            _position++;
        }

        if (_position >= _source.Length)
        {
            return Failure($"Unterminated field reference starting at position {start - 1}.");
        }

        string name = _source.Substring(start, _position - start);
        _position++;

        return string.IsNullOrWhiteSpace(name)
            ? Failure($"Empty field reference at position {start - 1}.")
            : Node(new FieldReferenceNode(name));
    }

    private Result<FormulaNode> ParseIdentifier(int depth)
    {
        int start = _position;

        while (_position < _source.Length && (char.IsLetterOrDigit(_source[_position]) || _source[_position] == '_'))
        {
            _position++;
        }

        string name = _source.Substring(start, _position - start);

        SkipWhitespace();

        // Not followed by '(' — a bare identifier is a field reference and a
        // literal keyword, never a variable. There is no variable namespace to
        // collide with, and no assignment to create one.
        if (_position >= _source.Length || _source[_position] != '(')
        {
            if (string.Equals(name, "TRUE", StringComparison.OrdinalIgnoreCase))
            {
                return Node(new LiteralNode(FormulaValue.FromBoolean(true)));
            }

            if (string.Equals(name, "FALSE", StringComparison.OrdinalIgnoreCase))
            {
                return Node(new LiteralNode(FormulaValue.FromBoolean(false)));
            }

            return Node(new FieldReferenceNode(name));
        }

        // An unknown function fails at PARSE time. Deferring it to evaluation
        // would mean an unknown name reaching a dispatch site, and a dispatch
        // site that accepts arbitrary names is the shape every sandbox escape
        // takes.
        if (!_functions.Contains(name))
        {
            return Failure($"'{name}' is not a known function.");
        }

        _position++;
        var arguments = new List<FormulaNode>();

        SkipWhitespace();
        if (_position < _source.Length && _source[_position] == ')')
        {
            _position++;
            return Node(new FunctionCallNode(name, arguments));
        }

        while (true)
        {
            Result<FormulaNode> argument = ParseExpression(depth + 1);
            if (argument.IsFailure)
            {
                return argument;
            }

            arguments.Add(argument.Value);

            SkipWhitespace();
            if (_position >= _source.Length)
            {
                return Failure($"Unterminated argument list for '{name}'.");
            }

            if (_source[_position] == ',')
            {
                _position++;
                continue;
            }

            if (_source[_position] == ')')
            {
                _position++;
                break;
            }

            return Failure($"Expected ',' or ')' at position {_position}.");
        }

        Result<int> arity = _functions.ValidateArity(name, arguments.Count);
        return arity.IsFailure
            ? Result.Failure<FormulaNode>(arity.Error!)
            : Node(new FunctionCallNode(name, arguments));
    }

    private FormulaOperator? TryReadComparison()
    {
        if (_position >= _source.Length)
        {
            return null;
        }

        char c = _source[_position];
        char next = _position + 1 < _source.Length ? _source[_position + 1] : '\0';

        switch (c)
        {
            case '=':
                _position++;
                return FormulaOperator.Equal;

            case '<' when next == '>':
                _position += 2;
                return FormulaOperator.NotEqual;

            case '<' when next == '=':
                _position += 2;
                return FormulaOperator.LessThanOrEqual;

            case '<':
                _position++;
                return FormulaOperator.LessThan;

            case '>' when next == '=':
                _position += 2;
                return FormulaOperator.GreaterThanOrEqual;

            case '>':
                _position++;
                return FormulaOperator.GreaterThan;

            default:
                return null;
        }
    }

    private void SkipWhitespace()
    {
        while (_position < _source.Length && char.IsWhiteSpace(_source[_position]))
        {
            _position++;
        }
    }

    private Result<FormulaNode> Node(FormulaNode node)
    {
        _nodes++;
        return _nodes > _limits.MaxNodes
            ? Failure($"The formula exceeds the maximum of {_limits.MaxNodes} nodes.")
            : Result.Success(node);
    }

    private static Result<FormulaNode> Failure(string message)
        => Result.Failure<FormulaNode>(new Error(
            ErrorCodes.ValidationFailed, message, ErrorCategory.Validation));
}
