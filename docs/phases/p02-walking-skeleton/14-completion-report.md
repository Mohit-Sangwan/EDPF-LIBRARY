# Phase 02 — Completion report (Gate G0: Viability)

**Phase:** p02 · **Date:** 2026-08-01 · **Squad:** All hands

## What was proven

The entire architecture, end to end, at the thinnest possible width: one
entity, two providers, two TFMs, one host. Produced
[ADR-012](../../adr/ADR-012-request-pipeline.md).

Every ADR from Phase 00 that could be falsified by implementation was
exercised in running code:

| ADR | How it was exercised | Held? |
|---|---|---|
| ADR-001 (wrap EF Core) | Repository over EF Core, two providers, no bespoke ORM | Yes |
| ADR-002 (tiered TFMs) | Kernel builds on five TFMs; skeleton on net8.0 + net10.0 | Yes |
| ADR-003 (outbox, no 2PC) | Outbox row committed with the state change; dispatched once | Yes |
| ADR-004 (tenancy) | Structural tenant filter; adversarial cross-tenant read → 404 | Yes |
| ADR-006 (crypto-shredding) | DEK destroyed → tombstone; chain still validates | Yes |
| ADR-007 (crypto-agility) | 35-byte self-describing envelope; verified byte-exact in the raw table | Yes |
| ADR-008 (test tiers) | Identical gate suite parameterized over both Tier A providers | Yes |
| ADR-012 (pipeline order) | Composed order asserted against the canonical order | Yes |

## Verification

**84 automated tests green** — 59 unit, 12 architecture, 12 skeleton
component, 1 conformance contract. **19/19 live gate checks passed** against
a real SQL Server instance; full evidence in
[09-test-plan.md](09-test-plan.md#execution-record--2026-08-01).

The demonstration that matters most is 6: after destroying a subject's key,
the patient read returns `[erased]` while chain verification still reports
`isValid = true`. That is the GDPR-erasure / immutable-audit / clinical-
retention conflict — Gap #18, severity C — resolved in code rather than in
prose.

## Findings from implementation

1. **Audit must commit inside the transaction, not after it.** The first
   implementation wrote the audit record after `SaveChangesAsync`, which
   would have allowed a committed create with no audit trail on a subsequent
   failure. Corrected so audit failure rolls the create back (BRL-005). This
   is exactly the class of defect Phase 02 exists to surface cheaply.

2. **Architecture tests must ignore comments.** The initial source scans for
   `DateTime.UtcNow` and `System.Security.Cryptography` flagged XML
   documentation that *names* the banned APIs while explaining the ban. The
   rules were narrowed to code lines rather than weakened — a rule that
   cannot distinguish code from prose would eventually be silenced.

3. **Analyzer set required explicit, justified exceptions** rather than
   inline suppressions; four rules are disabled repo-wide with reasons in
   `.editorconfig` (see [p01/14-completion-report.md](../p01-foundations/14-completion-report.md#decisions-taken-during-implementation)).

4. **No Phase 00 assumption was falsified.** No ADR required amendment.

## Shortcuts taken (all recorded, none load-bearing)

[TDL-0001](../../tdl/TDL-0001-skeleton-ensurecreated.md) `EnsureCreated`
instead of migrations · [TDL-0002](../../tdl/TDL-0002-outbox-log-transport.md)
log-entry outbox transport · [TDL-0003](../../tdl/TDL-0003-ephemeral-dev-keys.md)
ephemeral development keys.

The slice is marked **non-shippable**. Wave 2 rewrites it rather than
extending it; its value from here is as the program's canonical smoke test,
re-run at every wave.

## Exit criteria

- [x] All demonstrations pass, automated, on both providers.
- [x] ADR-012 order implemented and enforced by test.
- [x] No falsified Phase 00 assumption requiring re-decision.
- [ ] **Go/no-go decision recorded — sponsor action outstanding.**

**Gate G0 (Viability): PASSED on engineering criteria.** The remaining item
is the sponsor's recorded go/no-go. Per the Playbook this is the last cheap
exit point in the program; the engineering evidence for that decision is
complete and reproducible by running one script.
