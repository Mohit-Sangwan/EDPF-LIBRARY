# Phase 04 — Dependency Injection & Composition

**Status:** Complete · **Squad:** C · **Depends on:** Phases 01, 03

## Purpose

A registration model that makes correct wiring easy, incorrect wiring
impossible, and works identically in DI-native hosts (ASP.NET Core, Worker)
and DI-hostile ones (WebForms, WinForms, WPF).

## Constraining ADRs

ADR-002, ADR-011, ADR-013.
**Produces** [ADR-014](../../adr/ADR-014-composition-lifetime-policy.md).

## Scope

**In:** registration extensions, lifetime validation, scope management for
non-DI hosts, decorator support.
**Out:** replacing `Microsoft.Extensions.DependencyInjection`. EDPF *uses* a
container; it does not write one (Principle 0).

## Deliverables

| Deliverable | Location |
|---|---|
| `IEdpfBuilder` fluent, per-module registration | `src/Edpf.Extensions.DependencyInjection/IEdpfBuilder.cs` |
| `AddEdpfCore()` + lifetime policy | `src/Edpf.Extensions.DependencyInjection/EdpfBuilder.cs` |
| `CaptiveDependencyDetector` — mandatory | `src/Edpf.Extensions.DependencyInjection/Validation/` |
| `EdpfScopeAccessor` for legacy hosts | `src/Edpf.Extensions.DependencyInjection/Hosting/` |
| `EdpfServiceLocator` — the awkward, logged escape hatch | `src/Edpf.Extensions.DependencyInjection/Hosting/` |

## Why captive-dependency detection is mandatory here

A singleton capturing the scoped `ITenantContext` keeps one request's tenant
alive for every request that follows. In a general framework that is a
lifetime bug; in a multi-tenant healthcare framework it is a cross-tenant
data breach. The detector therefore sweeps the **whole** service collection
statically — including `IEnumerable<T>` injection — rather than relying on the
container's `ValidateScopes`, which only fires when the offending service
happens to be constructed.

## Exit criteria — status

- [x] Modules registrable independently and composably; `AddEdpfCore` is
      idempotent (a defect found by test during this phase — a fresh builder
      per call had been duplicating every registration).
- [x] Captive-dependency test passes **and fails correctly when violated** —
      three positive cases and two negative ones, including the framework's
      own graph.
- [x] `ValidateOnBuild` and `ValidateScopes` enabled in all environments, and
      the sweep runs before the container is built — proven live at Gate G1.
- [ ] **Scoping proven in WebForms/WinForms/WPF** — the adapters are written
      and unit-covered, but no Tier 3 host application exists yet to prove
      them in situ. Reference applications 6 and 7 (Z.17) close this at
      Phase 35.
- [ ] **Container build-time benchmark** — deferred to Phase 31 with the rest
      of the performance baseline; the graph is currently far too small for a
      meaningful measurement.
