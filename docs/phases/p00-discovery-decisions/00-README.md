# Phase 00 — Discovery, Decisions & Compliance Baseline

**Status:** Complete · **Gate:** G0 part 1 · **Squad:** All hands
**Depends on:** nothing

## Purpose

Convert an aspirational capability list into binding, quantified, falsifiable
engineering decisions. Three of the project-killing risks are resolved here
or not at all.

## Constraining ADRs

None inbound — this phase *produces* ADR-001 … ADR-010.

## Scope

**In:** decisions, requirements, threat model, classification, compliance
mapping, test strategy, NFR quantification.
**Explicitly out:** production code. Spikes are throwaway and are deleted,
never promoted.

## Deliverables

| # | Deliverable | Location |
|---|---|---|
| 1 | Vision & scope | [01-requirements.md](01-requirements.md) |
| 2 | Quantified NFR sheet | [01-requirements.md](01-requirements.md#nfr-sheet) |
| 3 | Threat model (STRIDE) | [07-security.md](07-security.md) |
| 4 | Data classification catalog | [../../compliance/data-classification.md](../../compliance/data-classification.md) |
| 5 | Compliance-control matrix | [../../compliance/compliance-control-matrix.md](../../compliance/compliance-control-matrix.md) |
| 6 | Test strategy | [ADR-008](../../adr/ADR-008-test-strategy.md) |
| 7 | Reference architecture (C4 L1–L3) | [02-hld.md](02-hld.md) |
| 8 | Risk register | [12-operations.md](12-operations.md#risk-register) |
| 9 | ADR-001 … ADR-010 | [../../adr/](../../adr/) |

## Exit criteria — status

- [x] Ten ADRs written and accepted (ADR-001 … ADR-010).
- [x] Zero `[DECISION NEEDED]` markers in the NFR sheet.
- [x] Four spikes resolved; findings recorded in
      [14-completion-report.md](14-completion-report.md); spike code deleted
      (its conclusions live on as the skeleton's tests).
- [x] Compliance matrix covers the named standards or de-scopes them with a
      reason.
