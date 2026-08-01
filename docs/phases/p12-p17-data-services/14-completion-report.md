# Wave 3 — Completion report (toward Gate G3: Services)

**Phases:** p12–p17 · **Date:** 2026-08-01 · **Squads:** A and C

## What was built

The tenancy machinery the whole framework was designed around, and the four
Wave 3 data paths — bulk, blob, cache, search — each added to the adversarial
isolation suite as it was introduced, plus validation as a security control.

## Verification

**527 automated tests green** — 447 unit, 47 isolation, 18 architecture, 12
walking-skeleton component, 3 conformance contract. Five TFMs build clean with
warnings as errors.

**All twelve isolation routes covered**, and the coverage is machine-checked:
`IsolationRoutes.All` enumerates them in code, `[CoversIsolationRoute]` marks
the covering classes, and `IsolationCoverageTests` fails the build if any
route is left without one. Phase 12 §⑦ requires every later phase that adds a
data path to extend this suite — a requirement that lives only in a document
decays, so this one is enforced by the build.

**Stampede protection is proven, not asserted**: 1,000 concurrent requests on
an expired key produce exactly one origin call; after expiry, a further 200
concurrent requests produce exactly one more; distinct keys do not block each
other, so one slow key cannot stall the cache.

## The three properties worth stating

Each replaces a discipline with a structural guarantee:

**A cross-tenant blob path cannot be constructed.** `BlobPath.Create` requires
a tenant and prepends it; each segment is validated independently, so a
separator inside a segment is a rejection rather than nested structure.
Eleven traversal and encoding forms are refused — **rejected, not
normalised**, because a value needing cleaning did not come from anywhere
legitimate.

**An unprefixed cache key cannot be constructed** for a tenant-scoped entity.
This is the leak that is easiest to introduce by accident, because
`"patient:" + id` looks obviously correct. Separators and glob wildcards are
refused inside key parts — a separator would let one key name another's
namespace, and a glob would let an invalidation sweep across tenants. The
single boundary-crossing shape, `CacheKey.Global`, demands a written
justification.

**A validation failure cannot echo attacker input.** Construction strips
control characters and encodes markup unconditionally, and bounds length.
Eleven hostile payloads are neutralised across message, field name and rule
name — field names arrive in the request body too, so they are as
attacker-influenced as values.

## Decisions taken during implementation

**`record struct` and `init` accessors are unavailable on Tier 3.** Both need
`IsExternalInit`, which does not exist on net472/net48;
`Edpf.Abstractions` may neither reference a polyfill package (EDPF0001) nor
use `#if` (EDPF0002). `BulkProgress` and `BulkRowFailure` are therefore
explicit readonly structs, and `SearchQuery` takes its scopes through the
constructor. This is the third time ADR-002's cost has surfaced as a visible
design decision rather than creeping conditional compilation, which is the
outcome the ADR was written to produce.

**Public API baseline regeneration is now a repo tool.** After the third
manual regeneration — and one stale-entry failure — it became
[`tools/update-public-api.ps1`](../../../tools/update-public-api.ps1), which
regenerates from empty and verifies convergence. Appending looks correct until
a signature changes, at which point the stale entry is invisible in a diff
that only adds lines.

**`BulkRowFailure` carries an error but no row content.** A failure report
from a PHI import must not become a PHI export.

## Carried forward to Gate G3

Every item needs a live backend; none can be honestly claimed from a unit
test:

| Requirement | Phase | Why not now |
|---|---|---|
| Provisioning and deprovisioning end-to-end incl. crypto-shred | 12 | Needs a live store and key vault |
| Per-tenant key rotation with zero downtime | 12 | Needs load plus a real KMS |
| Bulk throughput target; 50M-row flat-memory ingest | 13 | Live engine at scale |
| Bulk tenant/encryption/audit parity vs. single-row paths | 13 | The **rule** is stated in the contract; the assertion needs the native paths (`SqlBulkCopy`, `COPY`) |
| 5 GB streaming upload/download; WORM immutability; presigned-URL expiry | 14 | Needs real blob storage |
| Redis L2 with coherent pub/sub invalidation; cache-down degradation | 15 | Needs a Redis instance |
| Index lag under load; reindex via alias swap; cross-tenant facet leakage | 16 | Needs a search cluster |
| Fuzzing the validators | 17 | Fuzz corpora are a Z.19 release activity |
| Broker-level message routing isolation | 16, 26 | Needs the Phase 26 transports |

**Gate G3 is therefore NOT passed.** What is complete is the part that
determines whether the rest can be built safely: the contracts, the three
structural guarantees above, and an isolation suite that now fails the build
if a future phase forgets to extend it.

## A note on the shape of these last three waves

Waves 2 and 3 have both delivered contracts plus the safety properties that
cannot be retrofitted, and deferred the engine-bound verification. That is a
deliberate and repeated choice, not drift: Z.12 says a claimed capability is a
tested capability, and Golden Rule 4 forbids unmeasured claims. Writing
adapters that nothing here can exercise would produce exactly the hollow
"supported" that ADR-008 exists to prevent. The certification bar is built
first; the implementations are measured against it.
