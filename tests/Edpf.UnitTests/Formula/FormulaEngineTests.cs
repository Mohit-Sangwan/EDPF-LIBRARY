using Edpf.Abstractions.Metadata;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;
using Edpf.Formula;
using Edpf.Metadata;

namespace Edpf.UnitTests.Formula;

/// <summary>
/// Phase 08c — the formula engine's arithmetic and evaluation semantics.
/// </summary>
public sealed class FormulaEngineTests
{
    private static readonly FormulaEngine Engine = new();

    private static FormulaValue Eval(string source, TestFormulaContext? context = null)
    {
        Result<FormulaValue> result = Engine.Evaluate(source, context ?? TestFormulaContext.Empty());
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }

    private static Error Reject(string source, TestFormulaContext? context = null)
    {
        Result<FormulaValue> result = Engine.Evaluate(source, context ?? TestFormulaContext.Empty());
        Assert.True(result.IsFailure, "Expected the formula to be refused.");
        return result.Error!;
    }

    // ── decimal precision ──────────────────────────────────────────────────

    [Fact]
    public void Addition_OfValuesFloatingPointCannotRepresent_IsExact()
    {
        // The master document: "a rounding error in a dosage or an invoice is
        // not a cosmetic defect". In binary floating point 0.1 + 0.2 is
        // 0.30000000000000004; in decimal it is 0.3.
        Assert.Equal(0.3m, Eval("0.1 + 0.2").Number);
    }

    [Fact]
    public void RepeatedAddition_DoesNotAccumulateError()
    {
        // Ten additions of 0.1 in double gives 0.9999999999999999.
        Assert.Equal(1.0m, Eval("0.1+0.1+0.1+0.1+0.1+0.1+0.1+0.1+0.1+0.1").Number);
    }

    [Fact]
    public void CurrencyCalculation_RoundsToTheExpectedMinorUnit()
    {
        // A tax line an accountant would check by hand.
        Assert.Equal(20.24m, Eval("ROUND(168.65 * 0.12, 2)").Number);
    }

    [Fact]
    public void Power_UsesDecimalMultiplication_NotFloatingPointExponentiation()
    {
        // Math.Pow(1.1, 3) is 1.3310000000000004.
        Assert.Equal(1.331m, Eval("POWER(1.1, 3)").Number);
        Assert.Equal(1.331m, Eval("1.1^3").Number);
    }

    [Fact]
    public void Power_AndItsOperatorForm_AgreeExactly()
    {
        // Two implementations of exponentiation would eventually disagree.
        Assert.Equal(Eval("POWER(2.5, 4)").Number, Eval("2.5^4").Number);
    }

    [Fact]
    public void Sqrt_OfAPerfectSquare_IsExact()
    {
        Assert.Equal(4m, Eval("SQRT(16)").Number);
        Assert.Equal(1.5m, Eval("SQRT(2.25)").Number);
    }

    [Fact]
    public void DivisionByZero_IsRefused_NotInfinity()
    {
        // A dosage calculation that quietly yields infinity is worse than one
        // that refuses to answer.
        Assert.Contains("zero", Reject("1 / 0").Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── evaluation semantics ───────────────────────────────────────────────

    [Fact]
    public void OperatorPrecedence_FollowsArithmeticConvention()
    {
        Assert.Equal(7m, Eval("1 + 2 * 3").Number);
        Assert.Equal(9m, Eval("(1 + 2) * 3").Number);
        Assert.Equal(-8m, Eval("-2^3").Number);
    }

    [Fact]
    public void Power_IsRightAssociative_AsInEverySpreadsheet()
    {
        // 2^(3^2) = 512, not (2^3)^2 = 64.
        Assert.Equal(512m, Eval("2^3^2").Number);
    }

    [Fact]
    public void If_DoesNotEvaluateTheUntakenBranch()
    {
        // The guard an author expects: IF(divisor = 0, 0, total/divisor) must
        // not divide by zero when the divisor is zero.
        Assert.Equal(0m, Eval("IF(0 = 0, 0, 1/0)").Number);
    }

    [Fact]
    public void Aggregates_SkipBlanks_RatherThanCoercingThemToZero()
    {
        // An unrecorded weight is not a weight of zero. Averaging it in would
        // be a clinically wrong answer arrived at silently.
        var context = TestFormulaContext.WithValues(
            ("A", FormulaValue.FromNumber(10m)),
            ("B", FormulaValue.Blank),
            ("C", FormulaValue.FromNumber(20m)));

        Assert.Equal(15m, Eval("AVERAGE([A],[B],[C])", context).Number);
        Assert.Equal(2m, Eval("COUNT([A],[B],[C])", context).Number);
    }

    [Fact]
    public void Average_OfNothing_IsBlank_NotZero()
    {
        var context = TestFormulaContext.WithValues(("A", FormulaValue.Blank));

        Assert.Equal(FormulaValueKind.Blank, Eval("AVERAGE([A])", context).Kind);
    }

    [Fact]
    public void Median_OfAnEvenCount_IsTheMeanOfTheMiddleTwo()
    {
        Assert.Equal(2.5m, Eval("MEDIAN(1, 2, 3, 4)").Number);
        Assert.Equal(2m, Eval("MEDIAN(3, 1, 2)").Number);
    }

    [Fact]
    public void YearsBetween_CountsWholeElapsedYears_NotDayArithmetic()
    {
        // Days/365.25 drifts; a bare year subtraction says someone born on
        // 31 December is one year old the next day.
        var context = TestFormulaContext.WithValues(
            ("Born", FormulaValue.FromTimestamp(new DateTimeOffset(2000, 12, 31, 0, 0, 0, TimeSpan.Zero))),
            ("Today", FormulaValue.FromTimestamp(new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero))));

        Assert.Equal(0m, Eval("YEARSBETWEEN([Born],[Today])", context).Number);
    }

    [Fact]
    public void TextComparison_IsOrdinal_NotCultureSensitive()
    {
        // A billing rule must decide the same way in every region. Under a
        // Turkish culture, culture-aware matching makes "I" and "ı" equal.
        Assert.False(Eval("CONTAINS(\"FILE\", \"ı\")").Boolean);
        Assert.True(Eval("CONTAINS(\"FILE\", \"IL\")").Boolean);
    }

    [Fact]
    public void NumberLiterals_ParseInvariantly()
    {
        // A formula stored under one server's culture must evaluate
        // identically under another's.
        Assert.Equal(1.5m, Eval("1.5").Number);
    }

    [Fact]
    public void StringFunctions_BehaveAsASpreadsheetAuthorExpects()
    {
        Assert.Equal("HELLO", Eval("UPPER(\"hello\")").Text);
        Assert.Equal("hello", Eval("LOWER(\"HELLO\")").Text);
        Assert.Equal("ab", Eval("LEFT(\"abcdef\", 2)").Text);
        Assert.Equal("ef", Eval("RIGHT(\"abcdef\", 2)").Text);

        // MID is 1-based.
        Assert.Equal("bcd", Eval("MID(\"abcdef\", 2, 3)").Text);

        // Asking for more characters than exist returns what there is.
        Assert.Equal("abc", Eval("LEFT(\"abc\", 99)").Text);
    }

    [Fact]
    public void EscapedQuote_InATextLiteral_IsADoubledQuote()
    {
        Assert.Equal("say \"hi\"", Eval("\"say \"\"hi\"\"\"").Text);
    }

    // ── parse failures are errors, not exceptions ──────────────────────────

    [Theory]
    [InlineData("1 +")]
    [InlineData("(1 + 2")]
    [InlineData("\"unterminated")]
    [InlineData("[unterminated")]
    [InlineData("1 2")]
    [InlineData("@")]
    public void MalformedFormula_IsRefusedWithAPosition_NotThrown(string source)
    {
        Result<FormulaNode> result = Engine.Parse(source);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.Validation, result.Error!.Category);
    }

    [Fact]
    public void UnknownFunction_FailsAtParseTime_NotAtDispatch()
    {
        // A dispatch site that accepts arbitrary names is the shape every
        // sandbox escape takes.
        Result<FormulaNode> result = Engine.Parse("EVAL(\"anything\")");

        Assert.True(result.IsFailure);
        Assert.Contains("EVAL", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WrongArgumentCount_IsRefusedAtParseTime()
    {
        Result<FormulaNode> result = Engine.Parse("ROUND(1)");

        Assert.True(result.IsFailure);
        Assert.Contains("exactly 2", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownField_IsRefused_WithoutEnumeratingKnownFields()
    {
        var context = TestFormulaContext.WithValues(
            ("SecretRate", FormulaValue.FromNumber(1m)),
            ("Amount", FormulaValue.FromNumber(1m)));

        Error error = Reject("[Nonexistent] + 1", context);

        Assert.Contains("Nonexistent", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretRate", error.Message, StringComparison.Ordinal);
    }

    // ── formula versioning ─────────────────────────────────────────────────

    [Fact]
    public void Definition_ResolvesToTheVersionInEffectAtThatInstant()
    {
        // An invoice raised in 2024 must be reproducible from the tax rule
        // that applied in 2024.
        var engine = new FormulaEngine();
        DateTimeOffset y2024 = new(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset y2025 = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        engine.Register(new FormulaDefinition("Tax", "[Amount] * 0.10", y2024, y2025));
        engine.Register(new FormulaDefinition("Tax", "[Amount] * 0.12", y2025));

        var context = TestFormulaContext.WithValues(("Amount", FormulaValue.FromNumber(100m)));

        Assert.Equal(
            10m,
            engine.Evaluate(engine.Resolve("Tax", y2024.AddMonths(1)).Value.Source, context).Value.Number);
        Assert.Equal(
            12m,
            engine.Evaluate(engine.Resolve("Tax", y2025.AddMonths(1)).Value.Source, context).Value.Number);
    }

    [Fact]
    public void Definition_ThatDoesNotParse_IsRefusedAtRegistration()
    {
        // A formula that fails to parse when an invoice run reaches it has
        // already stopped the invoice run.
        Result result = new FormulaEngine().Register(
            new FormulaDefinition("Broken", "[Amount] * ", DateTimeOffset.UnixEpoch));

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Definition_OverlappingAnExistingVersion_IsRefused()
    {
        var engine = new FormulaEngine();
        engine.Register(new FormulaDefinition("Tax", "1", DateTimeOffset.UnixEpoch));

        Result second = engine.Register(new FormulaDefinition("Tax", "2", DateTimeOffset.UnixEpoch));

        Assert.True(second.IsFailure);
        Assert.Equal(ErrorCodes.Duplicate, second.Error!.Code);
    }

    // ── dependency analysis ────────────────────────────────────────────────

    [Fact]
    public void ReferencedFields_ReportsWhatAFormulaReads()
    {
        FormulaNode parsed = Engine.Parse("IF([A] > 0, [B] * [A], [C])").Value;

        Assert.Equal(["A", "B", "C"], FormulaEngine.ReferencedFields(parsed));
    }

    [Fact]
    public void DependencyGraph_OrdersComputedFieldsAfterWhatTheyRead()
    {
        var graph = new FormulaDependencyGraph();
        graph.Add("Total", ["Subtotal", "Tax"]);
        graph.Add("Tax", ["Subtotal"]);
        graph.Add("Subtotal", ["Quantity", "UnitPrice"]);

        List<string> order = [.. graph.Resolve().Value];

        Assert.True(order.IndexOf("Subtotal") < order.IndexOf("Tax"));
        Assert.True(order.IndexOf("Tax") < order.IndexOf("Total"));
    }

    [Fact]
    public void DependencyGraph_CircularReference_IsReportedNamingTheCycle()
    {
        // A stack overflow cannot be caught in .NET — it takes the process
        // down. Detecting the cycle before evaluation is the difference
        // between an error message and an outage.
        var graph = new FormulaDependencyGraph();
        graph.Add("A", ["B"]);
        graph.Add("B", ["C"]);
        graph.Add("C", ["A"]);

        Result<IReadOnlyList<string>> result = graph.Resolve();

        Assert.True(result.IsFailure);
        Assert.Contains("circular", result.Error!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("A → B → C → A", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DependencyGraph_SelfReference_IsRefused()
    {
        var graph = new FormulaDependencyGraph();
        graph.Add("Total", ["Total"]);

        Assert.True(graph.Resolve().IsFailure);
    }

    [Fact]
    public void DependencyGraph_Order_IsStableAcrossRuns()
    {
        // An unstable order makes a failing calculation reproduce only
        // sometimes, which is the worst kind of bug to be handed.
        static IReadOnlyList<string> Build()
        {
            var graph = new FormulaDependencyGraph();
            graph.Add("D", ["A"]);
            graph.Add("C", ["A"]);
            graph.Add("B", ["A"]);
            graph.Add("A", []);
            return graph.Resolve().Value;
        }

        Assert.Equal(Build(), Build());
    }
}

/// <summary>An in-memory field source for formula tests.</summary>
internal sealed class TestFormulaContext : IFormulaContext
{
    private readonly Dictionary<string, FormulaValue> _values =
        new(StringComparer.OrdinalIgnoreCase);

    private TestFormulaContext(IEntityMetadata metadata) => Metadata = metadata;

    public IEntityMetadata Metadata { get; }

    public static TestFormulaContext Empty()
        => new(new EntityMetadata("Calc", "CALC", []));

    public static TestFormulaContext WithValues(params (string Name, FormulaValue Value)[] values)
    {
        var fields = new List<IFieldMetadata>(values.Length);
        foreach ((string name, FormulaValue value) in values)
        {
            fields.Add(new FieldMetadata(
                name, name, ClrTypeOf(value), value.Classification,
                isFilterable: value.Classification < DataClassificationLevel.Confidential));
        }

        var context = new TestFormulaContext(new EntityMetadata("Calc", "CALC", fields));
        foreach ((string name, FormulaValue value) in values)
        {
            context._values[name] = value;
        }

        return context;
    }

    public FormulaValue Read(IFieldMetadata field)
        => _values.TryGetValue(field.Name, out FormulaValue value) ? value : FormulaValue.Blank;

    private static Type ClrTypeOf(FormulaValue value) => value.Kind switch
    {
        FormulaValueKind.Number => typeof(decimal),
        FormulaValueKind.Text => typeof(string),
        FormulaValueKind.Boolean => typeof(bool),
        FormulaValueKind.Timestamp => typeof(DateTimeOffset),
        _ => typeof(object),
    };
}
