# Phase 02 — Walking Skeleton (the vertical slice)

**Status:** Complete · **Gate:** G0 (Viability) · **Depends on:** Phases 00, 01

> ★ **NON-SHIPPABLE.** The slice lives in `samples/walking-skeleton`, is
> marked non-shippable, and is **rewritten — not extended —** by Wave 2
> (Phase 02 §⑧). Every shortcut it takes is recorded in the TDL.

## Purpose

Prove the entire architecture end-to-end with the thinnest possible slice,
*before* any layer is generalized. The original plan would have built ~15
phases of infrastructure before a single request completed a round trip;
every later phase now generalizes something already proven to work here.

## Constraining ADRs

All of Phase 00's, plus **produces**
[ADR-012](../../adr/ADR-012-request-pipeline.md).

## Scope

**In:** one entity (`Patient`, with a PHI-classified field), two providers
(SQL Server, PostgreSQL), two TFMs (net8.0, net10.0), one host (ASP.NET Core
Web API), one operation set (create, read-by-id, paged list) plus the erasure
and chain-verification endpoints the gate demonstrations require.

**Explicitly out:** generality of any kind. No provider abstraction beyond
what two providers require, no caching, no messaging, no bulk. Resisting
generalization here is the point — that is Wave 2's job.

## Deliverables

| Deliverable | Location |
|---|---|
| Config, DI, structured logging + OTel tracing | `Program.cs` |
| Tenant resolution (header strategy) | `Pipeline/TenantResolutionMiddleware.cs` |
| JWT authentication, one RBAC policy set | `Program.cs`, `Features/Patients/PatientEndpoints.cs` |
| Validation | `Pipeline/ValidationFilter.cs`, `Features/Patients/PatientContracts.cs` |
| Idempotency | `Pipeline/IdempotencyFilter.cs`, `Infrastructure/Consistency/EfIdempotencyStore.cs` |
| Repository + one local transaction | `Features/Patients/PatientRepository.cs` |
| Field encryption, per-subject DEK under tenant KEK | `Infrastructure/Security/` |
| Hash-chained audit + verifier | `Infrastructure/Audit/` |
| Outbox + dispatcher | `Infrastructure/Consistency/OutboxDispatcher.cs` |
| RFC 9457 error responses | `Pipeline/Problems.cs` |
| Docker harness (API + SQL Server + PostgreSQL + Jaeger + Seq) | [`docker-compose.yml`](../../../samples/walking-skeleton/docker-compose.yml) |
| Integration suite, both providers | `tests/Edpf.WalkingSkeleton.Tests/Gate/` |
| Benchmark baseline seed | `tests/Edpf.Benchmarks/` |

## Gate G0 demonstrations

See [09-test-plan.md](09-test-plan.md) for the mapping from each of the ten
required demonstrations to the automated test that proves it, and
[11-usage.md](11-usage.md) to run them live.

## Exit criteria — status

- [x] Ten demonstrations automated, running on both providers.
- [x] ADR-012 pipeline order implemented **and enforced by tests** (canonical
      order vs. composed order).
- [x] No Phase 00 assumption falsified — ADR-006's crypto-shredding
      reconciliation held under implementation, which was the main risk.
- [ ] **Go/no-go decision recorded** — sponsor action; this is the last cheap
      exit point in the program.
