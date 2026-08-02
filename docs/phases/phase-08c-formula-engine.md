# Phase 08c — Formula & Expression Engine

**Status:** Complete
**Gate contribution:** G2 (Data Core)
**ADR produced:** [ADR-026 — Formulas are a closed grammar over decimal](../adr/ADR-026-formula-sandbox.md)

## What this phase delivers

Excel-like, user-authorable formulas for billing rules, tax computation,
insurance package calculation, clinical scores and KPI definitions — with
field references resolving through the Phase 05b metadata platform, so a
formula can name a custom field a customer created this morning.

| File | Contents |
| --- | --- |
| [`FormulaParser.cs`](../../src/Edpf.Formula/FormulaParser.cs) | Recursive descent with depth and node ceilings applied *during* the parse |
| [`FormulaAst.cs`](../../src/Edpf.Formula/FormulaAst.cs) | The closed node hierarchy — `private protected` constructor |
| [`FormulaEvaluator.cs`](../../src/Edpf.Formula/FormulaEvaluator.cs) | Decimal arithmetic, classification propagation, step budget |
| [`FormulaLibrary.cs`](../../src/Edpf.Formula/FormulaLibrary.cs) | 38 functions, each a pure transformation of its arguments |
| [`FormulaFunctions.cs`](../../src/Edpf.Formula/FormulaFunctions.cs) | The closed registry; an unknown name fails at parse time |
| [`FormulaDependencyGraph.cs`](../../src/Edpf.Formula/FormulaDependencyGraph.cs) | Evaluation ordering with circular-reference detection |
| [`FormulaEngine.cs`](../../src/Edpf.Formula/FormulaEngine.cs) | Effective-dated definitions, field analysis, result classification |
| [`FormulaLimits.cs`](../../src/Edpf.Formula/FormulaLimits.cs) | The ceilings, each with the reason it exists |

## The three properties this phase is about

### 1. Decimal precision

> *"a rounding error in a dosage or an invoice is not a cosmetic defect"*

`0.1 + 0.2` is `0.3`, and ten additions of `0.1` are `1.0` — neither of which
is true in binary floating point. `SQRT` uses Newton-Raphson **in decimal** and
`POWER` uses repeated multiplication, because `Math.Sqrt` and `Math.Pow` route
through `double` and hand back the error the requirement exists to prevent.

`POWER(1.1, 3)` is `1.331`. `Math.Pow(1.1, 3)` is `1.3310000000000004`.

### 2. Sandbox

> *"A user-authored formula is untrusted input regardless of who authored it."*

The safety argument is **not** that the evaluator is careful — it is that the
capabilities are absent. There is no AST node for member access, indexing,
assignment, method invocation or type reference, so `[Amount].GetType()` does
not parse. The function registry is closed, so `EVAL(...)` fails before
evaluation begins rather than at a dispatch site.

`FormulaAssembly_ContainsNoEscapeCapability` keeps it that way: `System.IO`,
`System.Net`, `System.Reflection`, `Activator`, `Emit`, `Process`,
`Environment`, `Random` and ambient clocks must not appear in the assembly.
Twelve hostile sources — JNDI, shell, SQL and spreadsheet-injection shapes —
are asserted to be refused at parse time.

Exhaustion is treated separately from escape, because a formula that escapes
nothing can still take a node down: nesting, node count, source length, text
amplification, exponent magnitude and a step budget each have a ceiling.

**The step budget is deterministic on purpose.** A wall-clock timeout makes the
same formula pass on an idle machine and fail on a loaded one — a limit that
cannot be tested is a limit nobody can rely on. A wall-clock ceiling exists as
defence in depth; the step budget is what the tests verify.

### 3. Classification propagation

This one is not in the phase text, and it is the property that matters most
once Phase 05b is in place.

Without it, a formula is a laundering mechanism: read a PHI field, multiply by
one, and emit a result that no redactor, encryptor or export filter recognises
as protected. Every downstream control keys off classification.

So every operation takes the highest classification among its inputs —
including the **condition** of an `IF`, because which branch was taken is
itself information derived from the condition.
`IF([Weight] > 100, "high", "low")` returns a PHI-classified `"high"`.

`FormulaEngine.ResultClassification` answers the question *before* evaluation,
so a caller can decide where a computed value may be stored before computing
it — rather than discovering the answer once the value is already written
somewhere unprotected.

## Exit criteria

| Criterion | Status |
| --- | --- |
| Decimal arithmetic throughout, no floating point | Met — including SQRT and POWER |
| Bounded AST, no I/O / reflection / code generation | Met — enforced by an assembly-scope architecture test |
| Execution limits: depth, recursion, result size | Met — six ceilings, deterministic step budget |
| Circular-reference detection | Met — reported naming the cycle, before evaluation |
| Formula versioning with effective dating | Met |
| Formulas unit-testable before going live | Met — evaluation is deterministic; no clock, no randomness |
| Field references resolved through Phase 05b metadata | Met — including runtime-defined custom fields |

## Scope boundary

**UCUM unit-aware computation is not built.** The phase names it, and Phase 24
provides the unit converter, but wiring dimensional analysis through the
expression tree is a design question in its own right: whether `[DoseMg] +
[DoseMl]` should fail at parse time, at evaluation, or be coerced. Getting
that wrong in a dosage calculation is worse than not having it, and per Z.12 a
claimed capability is a tested capability. `FormulaValue` carries no unit
today, and adding one later is an additive change.

**The step budget is not calibrated.** 100,000 steps was chosen to be generous
for legitimate formulas rather than derived from measurement; Z.9's benchmark
gate does not yet cover formula evaluation.

**The sandbox is argued, not proven.** Absent capability plus an enumerated
hostile corpus, machine-checked — not a soundness proof. Independent security
review (G4) is a criterion that cannot be self-satisfied, and this engine is
squarely in its scope. It is the component in this repository most deserving
of that review.
