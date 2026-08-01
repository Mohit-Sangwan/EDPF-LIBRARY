# Wave 2 — Completion report (toward Gate G2: Data Core)

**Phases:** p06–p11 · **Date:** 2026-08-01 · **Squad:** A

## What was built

The provider contract and capability model, both Tier A dialects, the
injection-safe query compiler with offset and keyset pagination, the
connection-routing contracts, the saga coordinator, the concurrency and
migration contracts. Produced ADR-016 through ADR-021.

## Verification

**446 automated tests green** — 413 unit, 18 architecture, 12
walking-skeleton component, 3 conformance contract. Solution builds clean on
all five TFMs with warnings as errors.

The two claims worth stating precisely:

- **226 injection assertions pass**, and they assert something stronger than
  "no injection succeeded": a hostile value produces byte-identical SQL to a
  benign one. That is a structural property of ADR-018, not a sampling of
  known-bad patterns.
- **Twelve adversarial cross-tenant routes are blocked**, including the ones
  that usually work — OR-ing the predicate, filtering `TenantId` directly,
  and forging a keyset cursor.

## Decisions taken during implementation

**`IAsyncDisposable` does not exist on Tier 3 TFMs.** The migration lease was
originally `IAsyncDisposable`; adding `Microsoft.Bcl.AsyncInterfaces` to
`Edpf.Abstractions` would have broken the zero-package-reference rule
(EDPF0001), so the lease became an explicit `IMigrationLease` with
`RenewAsync`/`ReleaseAsync`. This is ADR-002's cost surfacing exactly where it
should — at a design decision, visibly — rather than through creeping `#if`.
The result is arguably better: releasing a distributed lock is I/O and
deserves to be visible at the call site.

**The filter compiler returns `Result`, not exceptions.** The first version
used an internal exception for control flow inside the visitor. CA1064
flagged it, and the rule was right: rewriting the visitor as
`IFilterVisitor<Result<string>>` removed the exception entirely and matches
Z.3 rule 8.

**`String.Replace(string, string, StringComparison)` is unavailable on
Tier 3.** LIKE-wildcard escaping is character-wise instead, which is faster
and avoids the analyzer.

**Public API baseline regeneration must be a rebuild, not an append.**
Changing the lease signature left a stale entry that failed with RS0017. The
baseline is now regenerated from empty each time the surface changes — worth
recording because the append approach looked fine until a signature changed.

## Carried forward to Gate G2

Stated plainly. Every item below needs a live engine, sustained load, or
fault injection, and none can be honestly claimed from a unit test:

| Requirement | Phase | Why not now |
|---|---|---|
| ~300-test round-trip conformance battery across all mapped types | 06 | Needs live engines; property-based type fidelity cannot be asserted against a dialect string |
| Two providers passing conformance 100% | 06 | Same |
| Abstraction overhead within budget vs. raw ADO.NET and Dapper | 06, 08 | Benchmarks need a real connection; belongs with the Phase 31 baseline |
| Chaos failover, replica latency, circuit recovery (Toxiproxy) | 07 | Fault injection against real endpoints |
| Pool-exhaustion graceful degradation | 07 | Load test |
| 10M-row stream with flat memory | 08 | Live engine |
| Outbox durability under process kill | 09 | Chaos test |
| Effectively-once under duplicate delivery | 09 | The mechanism is proven at unit level; the end-to-end claim needs a broker (Phase 26) |
| 100M-row zero-downtime migration under load | 11 | Live engine at scale |
| Ten-instance concurrent-startup safety | 11 | Needs a real distributed lock and orchestrator |
| Drift detection against a live schema | 11 | Live engine |

**Gate G2 is therefore NOT passed.** What is complete is the part that
decides whether the rest is buildable: the contracts every provider
implements, the safety properties that cannot be retrofitted, and the
certification bar itself. The engine-bound verification is scheduled work
against fixed contracts, not open design.

## Why this split is the honest one

The alternative was to write provider implementations that cannot be
exercised here and call the phase done. Z.12 states that a claimed capability
is a tested capability, and Golden Rule 4 forbids unmeasured claims. A
`SqlServerDataProvider` with no live conformance run would be exactly the
hollow "supported" that ADR-008 exists to prevent — so the certification bar
was built first, and the implementations will be measured against it.
