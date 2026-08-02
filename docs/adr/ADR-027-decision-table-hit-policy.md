# ADR-027 — A decision table declares its hit policy; ambiguity is refused, not resolved

- **Status:** Accepted
- **Date:** 2026-08-02
- **Phase:** 17c — Document, Print, Barcode & Rules Platform
- **Related:** [ADR-026](ADR-026-formula-sandbox.md) (the evaluator this builds on), [ADR-025](ADR-025-metadata-resolved-fields.md)

## Context

The rules platform was promoted out of the clinical vertical into core
(Appendix I.1): every vertical needs rules, not only healthcare. That
promotion brings two design questions the clinical version could have dodged.

**First: what happens when two rows of a decision table both match?**

Every rules engine has to answer this. The tempting answer is a sensible
default — first match wins, say — because it means an author never has to
think about it. That is exactly the problem. A table where two rows overlap
is usually a table whose author did not realise they could both be true, and a
sensible default converts their mistake into a confident wrong answer. In a
pricing table that is a mispriced contract; in a triage table it is a
mis-triaged patient.

**Second: where does the expression evaluator come from?**

Decision-table conditions look simpler than general formulas, so a small
purpose-built evaluator is the path of least resistance — right up until
someone needs a function call in a condition. ADR-026 already recorded "a
second expression evaluator appears anywhere in the codebase" as a revisit
trigger; this phase is the first thing to test it.

## Decision

**1. `HitPolicy` is a required constructor argument with no default value.**

Four closed options — `Unique`, `First`, `Priority`, `Collect` — and the
author must pick one. `Unique` treats an overlap as an authoring error and
fails, naming the rows that collided. `Priority` fails on a tie for the same
reason: which row wins would otherwise depend on declaration order.

**2. A table with no matching row and no declared fallback fails.**

Returning an empty result would leave the caller to interpret an absence, and
the usual interpretation is zero — which is free in a pricing table and none
in a dosage table. `Fallback` is opt-in, and choosing not to declare one means
"an uncovered input is an error I want to hear about".

**3. Every outcome names the rows that produced it.**

Not a debugging aid. Someone will be asked why this claim was denied, and
"the table said so" does not survive an audit or an appeal.

**4. Conditions and outcomes are Phase 08c formulas. There is no second
expression language.**

`Edpf.Rules` references `Edpf.Formula`, so the sandbox, the decimal
arithmetic and the classification propagation are inherited rather than
reimplemented. Two evaluators means two sandboxes, and the second is always
the weaker one — written under deadline, by someone who has not read the
threat model, for a case that "obviously" does not need one.

**5. Simulation reports gaps, overlaps and unreachable rows, not just
outcomes.**

An author supplies representative cases and gets back what the table would do
— plus the three findings that matter more than any individual answer. A
`Compare` between two versions answers the question an author actually has
before changing a live table: *which cases decide differently now?* A text
diff cannot answer that, because two rewritten conditions can be equivalent
and one changed constant can move thousands of cases.

### Enforcement

| Test | Prevents |
| --- | --- |
| `OnlyTheFormulaAssemblyParsesUserAuthoredExpressions` | A second expression front end appearing anywhere in `src/` |
| `RulesPlatform_ConsumesTheFormulaEngine_RatherThanItsOwn` | The reference being dropped in a refactor |
| `RuleConditions_CannotEscapeTheFormulaSandbox` | The inherited sandbox being bypassed |
| `UniquePolicy_WithOverlappingRows_IsRefusedRatherThanTiebroken` | Ambiguity being silently resolved |
| `PriorityPolicy_WithTiedPriorities_IsRefused` | The same ambiguity via a different route |

## Consequences

### Accepted costs

- **Authors must think about overlap at authoring time.** Choosing a hit
  policy is friction, and some authors will pick `First` to make the question
  go away. That is a worse outcome than `Unique` but a better one than a
  silent default, because it is at least recorded in the table.
- **`Unique` tables fail at runtime on inputs nobody simulated.** A table that
  is genuinely exhaustive and mutually exclusive will never fail; one that
  merely looks that way will fail on the day a real input finds the overlap.
  The simulator exists to move that day earlier, not to remove it.
- **The rules engine inherits every formula limitation.** No fractional
  exponents, no lookups against other tables, no user-defined functions. A
  rules author will hit these before a billing author does.

### What this does not claim

- **Simulation is only as good as the cases supplied.** `Analyze` cannot prove
  a table total — that would mean reasoning about the conditions symbolically,
  which the grammar permits but which is separate work. What it does is turn
  "we think this table is complete" into a claim checked against named
  examples.
- **`Compare` detects outcome changes over the supplied cases only.** A change
  that affects no sampled case reports clean. The correct reading is "no
  sampled case changed", not "nothing changed".

## Revisit triggers

- **A table needs a hit policy outside the four.** DMN defines others
  (`Any`, `Output order`, `Rule order`); if one is genuinely needed, adding it
  is an ADR rather than an enum entry, because each new policy is a new way
  for ambiguity to be resolved rather than reported.
- **Someone needs a condition the formula grammar cannot express.** The answer
  is to extend the grammar under ADR-026's own triggers — never to add a
  second evaluator for rules.
- **Symbolic completeness analysis becomes necessary.** If tables grow to
  where example-based coverage stops being credible, proving totality becomes
  worth its cost, and the analysis contract changes shape.
