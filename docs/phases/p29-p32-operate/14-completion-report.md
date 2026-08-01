# Wave 7 — Completion report (toward Gate G7: Operable)

**Phases:** p29–p32 · **Date:** 2026-08-01 · **Squads:** C + A + QA Architect

## What was built

I said at the start of this wave that it is the most infrastructure-bound
yet and to expect a higher deferral ratio. That held: two phases are almost
entirely drills and matrix runs. What was buildable is the **decision logic**
those drills feed — error budgets, burn-rate alerting, and the regression
gate — and that logic is worth having independently, because it is where
operational judgement is encoded.

| Phase | Delivered | Location |
|---|---|---|
| **30** | `ServiceLevelObjective` with error-budget maths, `BurnRateEvaluator` (multi-window, multi-burn-rate), `BurnRateDecision` | `src/Edpf.Operations/Slo/` |
| **31** | `BenchmarkBaseline` regression gate, `BenchmarkFinding` | `src/Edpf.Operations/Benchmarks/` |

## Verification

**773 automated tests green** — 693 unit, 47 isolation, 18 architecture, 12
walking-skeleton component, 3 conformance contract. Five TFMs build clean.

## Why burn-rate alerting rather than thresholds

Phase 30 is blunt that naive threshold alerts "are why on-call rotations
fail", and the implementation makes the reasoning concrete. "Alert when the
error rate exceeds 1%" fails in **both directions at once**:

- It pages at 3 a.m. for a ten-second blip that consumed 0.01% of the budget.
- It stays silent through a steady 0.35% burn that will exhaust the month's
  budget by the ninth day.

Engineers then learn to ignore it, which is how a real incident gets missed —
and Phase 30 is right that alert fatigue is a security risk, not merely an
annoyance.

Both failure modes are now tests. `Evaluate_BriefSpikeThatBarelyTouchesThe
Budget_DoesNotPage` and `Evaluate_SlowQuietBurn_RaisesATicketRatherThanBeing
Ignored` are the two halves, and the second is the one a threshold alert
cannot express at all.

The **short confirmation window** gets its own test
(`Evaluate_BurnStoppedButLongWindowStillElevated_DoesNotKeepFiring`) because
an alert that keeps firing for an hour after recovery teaches on-call to
ignore resolutions too.

Runbook and owner are **constructor-mandatory** on an SLO. Phase 30 says an
alert that cannot be acted on is deleted; making them required means such an
alert cannot be created in the first place.

## The regression gate

Z.9's 5% rule is what stops the slow rot: each phase costs two or three
percent, never enough to notice in review, and thirty phases later the
framework is quietly twice as slow with no single commit to blame.

Two details beyond the headline rule:

- **Allocation is gated as well as time**, and often matters more — a change
  that allocates twice as much can benchmark identically on an idle machine
  and then fall over under sustained load when the GC cannot keep up.
- **A renamed benchmark is a finding, not a pass.** Treating an unknown name
  as green is how a hot path stops being watched without anyone deciding to
  stop watching it.

## Carried forward to Gate G7

Almost all of this wave. Every item needs infrastructure, sustained time, or
the full provider matrix:

| Requirement | Phase | Why not now |
|---|---|---|
| **DR drill hitting declared RTO/RPO** | 29 | The Gate G7 criterion. A drill is an exercise against real infrastructure; nothing here can simulate it |
| Backup/restore verification | 29 | Same |
| Alert firing and resolution by fault injection | 30 | The **logic** is tested; firing needs Prometheus/Grafana and an injected fault |
| Cost attribution against actual cloud billing | 30 | Needs a billing account |
| Security detections (impossible travel, anomalous volume) | 30 | Needs a SIEM and simulated attack traffic |
| Every Phase 00 NFR target measured and recorded | 31 | Needs the load harness and pinned hardware |
| 72-hour soak, spike and stress tests | 31 | Needs 72 hours and a target environment |
| Full matrix: 13 databases × TFM tiers, Tier C included | 32 | Needs all thirteen engines |
| Mutation testing ≥ 60% (Stryker.NET) | 32 | Runs, but needs a long CI slot; not run here |
| Contract (Pact), property-based (FsCheck), fuzz (SharpFuzz) | 32 | Tooling not wired |
| Accessibility (axe-core, WCAG 2.2 AA) | 32 | Needs the Phase 35 reference UIs |
| Chaos: partition, latency, disk/memory pressure, clock skew | 32 | Needs fault injection |
| N-1 → N upgrade with data in place | 32 | Needs two deployed versions |

**Gate G7 is NOT passed.** Its headline criterion is a DR drill hitting the
declared RTO/RPO — an exercise, not a test.

## A note on the mutation-score threshold

Phase 32 sets ≥60% mutation score on core assemblies, with the reasoning that
"a high line-coverage number with a low mutation score means the tests assert
nothing, which is worse than no tests because it produces false confidence."

I have not run Stryker, so **I am not claiming a mutation score.** What I can
say about the 773 tests is narrower and honest: they were written to fail for
a reason — several caught real defects during development (the exception-
message PHI leak, the non-idempotent `AddEdpfCore`, the audit-ordering bug),
which is weak evidence they assert something. That is not a mutation score and
should not be read as one.
