# Phase 17c — Rules Platform & GS1 Barcode

**Status:** Partial — rules and barcode complete; documents and print deferred with reasons
**Gate contribution:** G3 (Services)
**ADR produced:** [ADR-027 — Decision-table hit policy](../adr/ADR-027-decision-table-hit-policy.md)

## What this phase is

A restructure rather than new scope: documents, print, barcode and rules were
inside the *clinical* vertical (24c), and every vertical needs them. Appendix
I.1 moves them to core.

This delivery covers the two halves that can be verified without hardware or a
rendering stack, and defers the two that cannot. See *Deferred* below.

## Rules platform

### `src/Edpf.Rules`

| File | Contents |
| --- | --- |
| [`DecisionTable.cs`](../../src/Edpf.Rules/DecisionTable.cs) | Tables, rows, hit policies, effective dating |
| [`RuleEngine.cs`](../../src/Edpf.Rules/RuleEngine.cs) | Evaluation, policy application, registration-time validation |
| [`RuleSimulator.cs`](../../src/Edpf.Rules/RuleSimulator.cs) | Simulation, coverage analysis, version comparison |

### The two decisions that matter

**Ambiguity is refused, not resolved.** `HitPolicy` is a required argument
with no default. A sensible default — first match wins — would mean an author
never has to think about overlap, and that is precisely the problem: an
overlapping table is usually one whose author did not realise two conditions
could both be true, and a default converts their mistake into a confident
wrong answer. `Unique` fails naming the colliding rows; `Priority` fails on a
tie, because otherwise declaration order would decide.

**There is no second expression language.** Conditions and outcomes are Phase
08c formulas. `Edpf.Rules` references `Edpf.Formula`, so the sandbox, the
decimal arithmetic and the classification propagation are inherited rather
than reimplemented — a rule reading a PHI field produces a PHI-classified
outcome, and `EVAL("1=1")` fails to register.

This was the first real test of ADR-026's *"a second expression evaluator
appears anywhere in the codebase"* revisit trigger. Writing a small
purpose-built evaluator for decision-table conditions was the path of least
resistance; `OnlyTheFormulaAssemblyParsesUserAuthoredExpressions` is what
makes the next person take the same path deliberately.

### Simulation

The phase requires a rule be testable before it goes live. `Analyze` reports
three findings that matter more than any individual outcome:

- **Gaps** — a case no row covers. With no fallback that is a runtime error
  waiting for the input that triggers it. A fallback hit counts as a gap too:
  it stops the error, but it does not mean the table covered the input.
- **Overlaps** — a case several rows match.
- **Unreachable rows** — a row no case reached. Suppressed under `First`,
  where later rows are legitimately never evaluated; reporting them would be a
  false finding, and false findings are how a report gets ignored.

`Compare` answers the question an author actually has before changing a live
pricing table: *which cases decide differently now?* A text diff cannot — two
rewritten conditions can be equivalent, and one changed constant can move
thousands of cases.

## GS1 barcode

### `src/Edpf.Barcode`

> *"GS1-128 and DataMatrix (GS1 is mandatory for medication and specimen
> traceability)."*

Traceability is why the **encoding** lives here and rendering does not. A
specimen label with a wrong check digit is a mislabelled sample; a lot number
that ran into an expiry date because a separator was missing is a medication
whose expiry a scanner never sees. Turning symbol values into ink has a
different failure mode and belongs where the printers are.

The safety-critical behaviours, each tested:

- **Variable-length fields get an FNC1 separator; fixed-length fields do
  not.** Encode lot `ABC` then expiry `(17)260801` without a separator and a
  scanner reads the lot as `ABC17260801` and finds **no expiry at all** — an
  expired medication scans as one that never expires.
- **A separator inside a value is refused, not escaped.** It would terminate
  the field early and everything after it would be read as a different field.
  This is the injection equivalent for barcodes, and there is no escape
  sequence for FNC1 to escape it to.
- **An unknown Application Identifier is refused rather than passed through.**
  Passing it through means guessing its length, and guessing wrong misreads
  every field after it.
- **The AI reader takes the longest match.** AI `24` does not exist but `240`
  and `241` do; a shortest-match reader rejects perfectly valid data.
- **Check digits are weighted 3,1 from the right.** Anchoring at the left
  passes GTIN-14 tests and fails GTIN-13, whose payload is odd-length — both
  lengths are tested for exactly that reason.

Check-digit and Code 128 checksum expectations come from GS1's and the Code
128 specification's own worked examples, not from what this implementation
produces. **One of my invented test vectors had the wrong check digit and the
implementation caught it** — which is the right way round.

## Exit criteria

| Criterion | Status |
| --- | --- |
| Rules: decision tables, effective dating, simulation, testing | Met |
| Rules: constrained expression only, no scripting host | Met — inherited from ADR-026, enforced by architecture test |
| Barcode: GS1-128 with correct AI and separator handling | Met |
| Barcode: check digits verified against published examples | Met |
| Documents: generation, merge fields, template sandboxing | **Deferred** |
| Print: queue, ZPL/EPL, network discovery | **Deferred** |

## Deferred, with reasons

**Documents.** PDF/A, Word and Excel generation needs a rendering library —
a package dependency whose licence must clear the ADR-009 gate, and whose
output can only be verified by opening it. The interesting security property
(template sandboxing against SSTI) is the same argument ADR-026 already makes,
and would be better built on the same evaluator than beside it. Doing that
properly is its own phase.

**Print.** `IPrinterProvider`, `IPrintQueue` and network printer discovery are
I/O against hardware. ZPL string generation is testable, but a print queue
that has never driven a printer is a claim, not a capability (Z.12), and
Phase 24f already owns the Tier 3 host where the hardware lives.

**DataMatrix.** Named alongside GS1-128 in the phase. The element-string layer
built here is symbology-independent and is what DataMatrix would encode; the
Reed-Solomon error correction and matrix placement are a separate body of work
with their own published test vectors, and half of it would be worse than
none.
