# ADR-026 — Formulas are a closed grammar over decimal, never a scripting host

- **Status:** Accepted
- **Date:** 2026-08-02
- **Phase:** 08c — Formula & Expression Engine
- **Related:** [ADR-025](ADR-025-metadata-resolved-fields.md) (field resolution), [ADR-018](ADR-018-query-construction-safety.md) (the same instinct applied to queries), [ADR-015](ADR-015-telemetry-redaction-policy.md) (redaction)

## Context

Billing rules, tax computation, insurance package calculation, clinical scores
and KPI definitions all need user-authorable expressions. The obvious
implementation — embed a scripting engine — is the one the master document
forbids outright:

> *"Constrained expression evaluation only — **no scripting host, ever** (an
> embedded script engine in a clinical system is a remote-code-execution
> surface)."*

And the constraint that shapes the arithmetic:

> *"decimal-precision arithmetic (never floating point — a rounding error in a
> dosage or an invoice is not a cosmetic defect)"*

Two threats sit behind this, and they need different defences:

1. **Escape** — the formula reaches I/O, reflection or code generation. A
   spreadsheet-style `INDIRECT` or `WEBSERVICE` is enough; so is any function
   that resolves a name to a type.
2. **Exhaustion** — the formula escapes nothing but consumes the server.
   `POWER` nested a few times, or concatenation doubling a string twenty
   times, does not need a sandbox escape to take a node down.

There is a third, subtler one that only appears once fields are classified:

3. **Laundering** — the formula reads a PHI field, computes something trivial,
   and emits a result nothing recognises as protected. Every downstream
   control (redaction, encryption, export filtering) keys off classification,
   so a result that arrives unclassified is a result that arrives unprotected.

## Decision

**A formula is a closed grammar evaluated over `decimal`, in an assembly that
does not contain the capabilities an escape would need.**

1. **The AST hierarchy is closed.** `FormulaNode`'s constructor is
   `private protected`, so no assembly outside `Edpf.Formula` can introduce a
   node the evaluator was not written to handle. There is no node for member
   access, indexing, assignment, method invocation or type reference — **the
   absence is the sandbox**, not a filter over a richer grammar.

2. **The function registry is closed and checked at parse time.** An unknown
   name fails before evaluation begins. A dispatch site that accepts arbitrary
   names is the shape every sandbox escape takes, so there isn't one.

3. **Arithmetic is `decimal` throughout.** `SQRT` uses Newton-Raphson in
   decimal and `POWER` uses repeated multiplication, because `Math.Sqrt` and
   `Math.Pow` route through `double` and hand back the binary rounding error
   this decision exists to keep out of a dosage.

4. **Classification propagates.** Every operation takes the highest
   classification among its inputs, including the *condition* of an `IF` —
   which branch was taken is itself information derived from the condition.
   `FormulaEngine.ResultClassification` answers the question before evaluation,
   so a caller knows where a computed value may be stored before computing it.

5. **Limits are a deterministic step budget, not a wall clock.** A wall-clock
   timeout makes the same formula pass on an idle machine and fail on a loaded
   one; a limit that cannot be tested is a limit nobody can rely on. A
   wall-clock ceiling exists as defence in depth, but the step budget is the
   control that is verified.

6. **Evaluation is deterministic.** No `NOW`, no `TODAY`, no `RAND`. The phase
   requires that a formula be unit-testable before it goes live, and a formula
   whose answer changes between runs is not.

### Enforcement

| Test | Prevents |
| --- | --- |
| `FormulaAssembly_ContainsNoEscapeCapability` | I/O, reflection, code generation, process launch or ambient state entering the assembly |
| `FormulaAssembly_TakesNoPackageDependency` | A transitive dependency reintroducing them |
| `HostileSource_IsRefusedAtParseTime` | 12 enumerated attack strings, including JNDI, shell and SQL shapes |
| `AstHierarchy_CannotBeExtendedFromOutsideTheAssembly` | The bounded-AST claim decaying |
| `ClassificationSurvivesEveryOperatorAndFunction` | 19 operations each dropping classification |

## Consequences

### Accepted costs

- **Authors get less than a spreadsheet.** No `INDIRECT`, no user-defined
  functions, no cell ranges, no lookups against other tables. Each of those is
  a real request that will be made, and each needs its own decision rather
  than a general escape hatch.
- **`POWER` takes whole-number exponents only.** A fractional exponent cannot
  be computed exactly in decimal, and computing it in `double` would reintroduce
  the defect. Compound-interest formulas needing `1.05^0.5` are not expressible
  and will need a decision of their own.
- **Blanks are skipped, not coerced to zero.** `AVERAGE` of nothing is blank
  rather than `0`. This differs from some spreadsheets and will surprise
  someone — but an unrecorded weight is not a weight of zero, and averaging it
  in is a clinically wrong answer arrived at silently.
- **The step budget prices operations uniformly.** A step is a step whether it
  is an addition or a 64-iteration square root, so the budget is a crude
  proxy for cost. It is a deterministic crude proxy, which is the property
  that matters most.

### What this does not claim

- **This is not a proof of sandbox soundness.** It is an argument from absent
  capability plus an enumerated corpus of hostile inputs, machine-checked. A
  determined reviewer might find a route neither the corpus nor the forbidden
  list anticipates. Per G4, independent security review is a criterion that
  cannot be self-satisfied, and this engine is squarely in its scope.
- **The step budget is not calibrated against real workloads.** The default of
  100,000 steps was chosen to be generous for legitimate formulas rather than
  derived from measurement. Z.9's benchmark gate does not yet cover formula
  evaluation.
- **Classification propagation is verified through the engine, not end to end.**
  That a PHI-derived result is *marked* PHI is tested; that every downstream
  store then honours the mark depends on adapters that remain
  infrastructure-unverified (Z.12).

## Revisit triggers

- **A legitimate formula needs a fractional exponent.** The decimal-exactness
  rule and that requirement cannot both hold; which one yields is an ADR.
- **Anyone proposes a function that reads anything its arguments do not
  contain.** Lookup against a reference table is the likely first request, and
  it needs its own decision about where that table comes from and who may read
  it — not an extension to the registry.
- **The step budget causes a legitimate formula to fail.** That means the
  budget is a bad cost model, not that it should be raised silently.
- **A second expression evaluator appears anywhere in the codebase.** The
  rules engine (Phase 17c) must reuse this one; two evaluators mean two
  sandboxes, and the second will be the weaker.
