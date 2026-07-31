# Phase 00 — Completion report (Gate G0 part 1)

**Phase:** p00 · **Date:** 2026-08-01 · **Squad:** All hands

## Deliverables

| Deliverable | Status | Evidence |
|---|---|---|
| Vision & scope | Complete | [01-requirements.md](01-requirements.md) |
| Quantified NFR sheet (15 entries, no gaps) | Complete | [01-requirements.md](01-requirements.md#nfr-sheet) |
| Threat model, 18 STRIDE entries | Complete | [07-security.md](07-security.md) |
| Data classification catalog | Complete | [../../compliance/data-classification.md](../../compliance/data-classification.md) |
| Compliance-control matrix, 24 controls | Complete | [../../compliance/compliance-control-matrix.md](../../compliance/compliance-control-matrix.md) |
| Test strategy | Complete | [ADR-008](../../adr/ADR-008-test-strategy.md) |
| C4 L1–L3 | Complete | [02-hld.md](02-hld.md) |
| Risk register, 18 risks | Complete | [12-operations.md](12-operations.md) |
| ADR-001 … ADR-010 | Accepted | [../../adr/](../../adr/) |

## Spike findings

Decisions were verified by spike, not debate. Spike code was deleted; its
conclusions survive as permanent tests.

**Spike-A — data core (decides ADR-001).** Identical query through EF Core,
Dapper and raw ADO.NET. Finding: EF Core's overhead is immaterial next to the
per-field crypto and audit costs EDPF adds — the differentiating work
dominates the data-access work, which is precisely the Principle 0 argument
for wrapping rather than building. Dapper is admitted per hot path on
benchmark evidence only.

**Spike-B — multi-target (decides ADR-002).** Compiling async + streaming +
modern crypto against net472 and net10.0. Finding: the divergence list is
real but finite (`IAsyncEnumerable` streaming, minimal-API hosting, OTel
auto-instrumentation, several BCL throw-helpers and `TimeProvider`).
Containing it behind `Edpf.Compatibility` keeps every other assembly clean —
now enforced by rule EDPF0002 and proven by five green TFM builds. The
throw-helper gap is visible in `.editorconfig`: CA1510-CA1513 are disabled
repo-wide precisely because the helpers do not exist on Tier 3.

**Spike-C — crypto-shredding (decides ADR-006).** Write audit records
referencing a subject, destroy the DEK, verify the subject's data is
unrecoverable *and* the chain still validates. Finding: the mechanism holds,
provided audit stores **tokens, never identifiers**, and the tokenizer salt
is itself a destroyable key. Both invariants are now code, and both are
asserted by tests that fail if either regresses
(`Verify_AfterSubjectErasure_ChainStillValid`,
`Write_Subject_NeverStoresRawIdentifier`).

**Spike-D — distributed transactions (closes the 2PC question).** Attempted a
transaction spanning SQL Server and MongoDB. Finding: no shared coordinator
exists across the target store set; the failure is structural, not a
configuration gap. The question is closed permanently in ADR-003; the outbox
is the answer.

## Exit criteria

- [x] Ten ADRs accepted.
- [x] Zero `[DECISION NEEDED]` markers remain.
- [x] Four spikes completed with written findings; spike code deleted.
- [x] Compliance matrix covers the named standards or de-scopes with reason.

**Gate G0 part 1: PASSED.** Proceed to Phase 01.
