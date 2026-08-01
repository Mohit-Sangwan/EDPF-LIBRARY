# Wave 6 — Completion report (toward Gate G6: Integration)

**Phases:** p25–p28 · **Date:** 2026-08-01 · **Squad:** C

## What was built

The two Wave 6 phases that are provable without brokers or a cluster:
internationalization, and feature flags. Both were absent from the original
specification entirely, and both are hard blockers — i18n for public-sector
procurement outside English-speaking jurisdictions, flags for safe rollout of
everything else.

| Phase | Delivered | Location |
|---|---|---|
| **27** | `Money` + `CurrencyService` (ISO 4217 minor units), `TextService` (ordinal-vs-cultural), `ZonedInstant` + `TimeZoneService` | `src/Edpf.Globalization/` |
| **28** | `FeatureFlag`, `FeatureManager` with stable bucketing, kill switches, stale-flag detection | `src/Edpf.Globalization/FeatureFlags/` |
| **25** | DST classification (`LocalTimeKind`) — the scheduling-safety primitive | `src/Edpf.Globalization/TimeZoneService.cs` |

## Verification

**745 automated tests green** — 665 unit, 47 isolation, 18 architecture, 12
walking-skeleton component, 3 conformance contract. Five TFMs build clean.

## Four properties worth stating

**The Turkish-i defect is asserted, not just avoided.** A test demonstrates
that `"FILE".ToLower(tr-TR)` yields `"fıle"` — dotless ı — and therefore never
equals `"file"`. The rule that follows is enforced by `TextService`:
**comparison for identity is ordinal; comparison for a human is cultural**,
and `ToLower()` is correct for neither. This defect has produced
authentication bypasses and failed config lookups for decades; the test is
there so the reason for the rule stays visible.

**Currency minor units are per-currency.** Hardcoding two decimal places
loses a factor of ten on a Kuwaiti dinar and invents decimals for yen. Adding
two currencies is refused — it is a question about an exchange rate on a date,
not an addition — and rounding is banker's, because repeated half-up rounding
across many transactions introduces a systematic upward bias an auditor
eventually finds.

**A DST-ambiguous time is reported, not silently resolved.** Phase 25 names
the defect precisely: a job scheduled at 02:30 that runs twice or not at all
across a DST transition. `ClassifyLocalTime` returns `Skipped` or `Repeated`
so the caller decides, rather than the framework quietly picking one. Both are
tested against real 2026 UK transitions. Historical rules are honoured too — a
2015 summer London instant resolves to BST and a winter one to GMT, which a
single stored offset could not do.

**Feature-flag bucketing is stable and monotonic.** A tenant's position comes
from FNV-1a over flag name plus tenant id, not `string.GetHashCode()`, whose
per-process randomisation would put a tenant inside the rollout on one node
and outside it on another. Tests assert that widening 1% → 10% **never
withdraws** the feature from a tenant that already had it, that a tenant does
not flap between evaluations, and that the kill switch beats every other rule
including an explicit allow-list.

## A justified analyzer exception

CA1309 (use ordinal comparison) fired on `TextService.CompareForDisplay` —
the one method whose entire contract is "compare the way this culture sorts",
because Swedish sorts `ä` after `z` while German sorts it with `a`. The
exception is path-scoped to that single file with the reasoning recorded, so
the rule keeps protecting every other comparison in the repository. That is the
third such exception in the codebase and each one is narrow and explained.

## Carried forward to Gate G6

| Requirement | Phase | Why not now |
|---|---|---|
| Hangfire/Quartz integration, leader election under node failure | 25 | Needs a cluster and a job store |
| Long-running job survival across a rolling deployment | 25 | Needs an orchestrator |
| **Broker-down chaos test with zero message loss** | 26 | The Gate G6 criterion; needs RabbitMQ/Kafka/Service Bus |
| Duplicate-delivery effectively-once, schema evolution, trace continuity across the broker | 26 | Same |
| Tenant-scoped topic/queue routing | 26 | Contract-level coverage exists in the isolation suite; the broker assertion needs the transports |
| SignalR hubs, backplane, mobile push | 26b | Needs Redis/Azure SignalR and device endpoints |
| Resource-based localization across 12 locales | 27 | Needs the resource assets; the **mechanisms** they exercise (culture resolution, collation, normalisation) are tested |
| Flag evaluation sub-microsecond benchmark | 28 | Belongs with the Phase 31 baseline; the no-network-call property is structural — `FeatureManager` reads a snapshot and has no I/O path |

**Gate G6 is NOT passed.** Its headline criterion is a broker-down chaos test,
which is by definition an integration test.

## Note on Phase 25's tenant-context requirement

Phase 25 states that tenant-context propagation into jobs is mandatory and
that the isolation suite must be extended with a job-context test. That test
already exists — `AmbientContextRouteTests.TenantScope_BackgroundWork_
StartsWithNoAmbientTenant`, added in Wave 3 under the
`BackgroundJobContext` route — because the isolation suite enumerates its
routes in code and Wave 3 covered all twelve. The requirement was satisfied
before the phase that raised it, which is what the coverage check is for.
