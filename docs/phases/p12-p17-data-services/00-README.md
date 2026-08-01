# Wave 3 — Data Services (Phases 12–17)

**Status:** Isolation-critical work complete; backend adapters carried
forward · **Gate:** G3 (Services) · **Squads:** A and C in parallel

## Constraining ADRs

ADR-004 (tenancy), ADR-006/007 (crypto), ADR-010 (residency), ADR-012
(pipeline), ADR-016/018/020 (data core).

## The organising principle

Phase 12 §④ names the adversarial isolation suite **"the single most
important test asset in the program"**, and Phase 12 §⑦ makes extending it a
mandatory Definition-of-Done line item for every later phase that adds a data
path. Wave 3 adds four such paths — bulk, blob, cache, search — so this wave
is where that requirement is either honoured or quietly dropped.

It is honoured, and made checkable: `IsolationRoutes.All` enumerates the
twelve routes in code, `[CoversIsolationRoute]` marks the classes covering
each, and `IsolationCoverageTests` **fails the build** if any route has no
covering class. A requirement that lives only in a document decays; this one
cannot.

## What this increment delivers

| Phase | Delivered | Location |
|---|---|---|
| **12** | `ITenantProvisioner` (provision as a resumable saga incl. isolation verification), `ITenantKeyProvider`, `TenantQuota`, `IBreakGlassService` + time-bounded justified grants | `src/Edpf.Abstractions/Tenancy/ITenantProvisioner.cs` |
| **13** | `IBulkInserter<T>`, `BulkOptions`, explicit `BulkFailurePolicy`, `BulkResult`/`BulkRowFailure` carrying no row content | `src/Edpf.Abstractions/Data/IBulkOperations.cs` |
| **14** | `BlobPath` — cross-tenant paths **unconstructable**; traversal rejected, not normalised | `src/Edpf.Abstractions/Storage/BlobPath.cs` |
| **15** | `CacheKey` — unprefixed tenant keys unconstructable; `MemoryCacheProvider` with real stampede protection; `CacheKeyBuilder` | `src/Edpf.Abstractions/Caching/`, `src/Edpf.Caching/` |
| **16** | `ISearchIndex<T>`, `SearchQuery` (tenant required by constructor), `SearchResults` with post-trimming counts | `src/Edpf.Abstractions/Search/ISearchIndex.cs` |
| **17** | `ValidationFailure` that cannot carry attacker-supplied content; `ValidationOutcome` with severity | `src/Edpf.Abstractions/Validation/ValidationFailure.cs` |

## Three properties made structural

**A cross-tenant blob path cannot be constructed.** `BlobPath.Create`
requires a tenant, prepends it, and validates each segment independently —
so a separator inside a segment is a rejection rather than nested structure.
Traversal is refused, not normalised: a value needing cleaning did not come
from anywhere legitimate.

**An unprefixed cache key cannot be constructed** for a tenant-scoped
entity. Cache-key collision is among the easiest cross-tenant leaks to
introduce by accident — `"patient:" + id` looks obviously correct — so the
key type refuses to produce one. The single shape that crosses the boundary,
`CacheKey.Global`, demands a written justification.

**A validation failure cannot echo attacker input.** `ValidationFailure`
holds a field name, a rule name, and a sanitised bounded message; construction
strips control characters and encodes markup unconditionally. Validation is a
security control, not a UX one — a message that reflects raw input is a
reflected-XSS and log-injection vector.

## Exit criteria — status

- [x] Twelve isolation routes enumerated in code with machine-checked coverage.
- [x] Blob, cache, search and validation paths added to the suite as Wave 3
      introduced them.
- [x] Stampede protection proven: 1,000 concurrent requests on an expired key
      produce exactly one origin call.
- [ ] **Gate G3 — the full suite against live backends** (Redis, an index
      cluster, real blob storage, a broker). See
      [14-completion-report.md](14-completion-report.md).
