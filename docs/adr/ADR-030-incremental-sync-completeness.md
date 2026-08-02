# ADR-030 — Incremental sync is gap-free by construction, or it is not a sync

- **Status:** Accepted
- **Date:** 2026-08-02
- **Phase:** 26f — Enterprise Connector Framework
- **Related:** [ADR-019](ADR-019-idempotency-contract.md) (idempotency), [ADR-013](ADR-013-configuration-precedence.md) (secrets), [ADR-017](ADR-017-connection-routing-resilience.md) (resilience)

## Context

A connector framework's job is to make each integration *"configuration plus a
thin adapter rather than a bespoke project"*. The value is not in the HTTP
plumbing — every team can write that. It is in the three things every bespoke
integration gets wrong, in the same way, and does not find out about.

All three share a failure mode: **the sync reports success and the data is
incomplete.** Nothing throws, nothing alerts, and the gap is discovered months
later by someone reconciling totals.

**1. The timestamp-only watermark.** Given a watermark of `12:00:00` and three
records stamped `12:00:00`, a query using `modified > watermark` loses all
three permanently. Switching to `>=` re-reads the boundary on every run
forever, which is survivable only if everything downstream is idempotent.

**2. Reading up to "now".** A transaction starts at `11:59:58`, stamps its row
`11:59:58`, and commits at `12:00:03`. A sync at `12:00:00` reads to
`12:00:00` and moves its watermark there. The row becomes visible three
seconds later carrying a timestamp *behind* the watermark — never read. The
same shape appears with clock skew and with replica lag.

**3. Offset pagination over a live set.** Read rows 0-99, then 100-199. A
delete before position 50 shifts everything up one, so the row formerly at 100
is now at 99 and is never read.

## Decision

**The framework makes each of the three structurally hard to get wrong, and
refuses the configurations that guarantee loss.**

1. **A cursor is composite: `(timestamp, id)`.** The comparison is
   `(ts > last.Ts) OR (ts = last.Ts AND id > last.Id)` — gap-free and
   duplicate-free even when a thousand records share a timestamp. Implemented
   once, in `SyncCursor.IsAfter`, so no connector author reconstructs it. Ids
   compare ordinally, because a culture-aware comparison would order them
   differently in another region.

2. **A cursor cannot move backwards.** `Advance` fails when handed a record at
   or before the current position, turning an ordering bug in a connector into
   a visible failure rather than a duplicate storm.

3. **The safety lag is mandatory and has no default.** `WatermarkPlanner`
   throws on a zero or negative lag. A default would be chosen once by whoever
   wrote the framework and never revisited by anyone integrating a source with
   five-minute batch transactions. Making the caller state a number forces
   them to think about their source's longest transaction. `ConservativeDefault`
   is offered as a starting point and documented as *not* a guarantee.

4. **The planner takes the source's clock, not the reader's.** Using the
   reader's would reintroduce skew as exactly the error the lag exists to
   absorb.

5. **Window bounds are re-checked per record.** A source that ignores or
   mis-applies its bounds would otherwise advance the cursor past records the
   pass never read. Rejections are surfaced in the run record rather than
   logged, because a non-zero count invalidates the completeness argument.

6. **Offset pagination requires an explicit `sourceIsFrozen` assertion.** A
   nightly extract against a snapshot is a real and safe use, so it is
   permitted — it just has to be *said*, because the silent default of
   "offset is fine" is how the skip happens.

7. **`Retry-After` beats computed backoff, always, including above our own
   ceiling.** The source is the only party that knows when its limit resets;
   our ceiling is a guess about its recovery and its header is a statement
   about it.

8. **Jitter is deterministic, derived from attempt and a per-connector seed.**
   Without jitter a fleet retries in lockstep and the synchronised herd is
   often what keeps a recovering source down. Deriving it rather than drawing
   it keeps a connector's schedule reproducible in a test and in an incident
   review.

9. **401, 403 and 400 are fatal, not retried.** Retrying adds load to a source
   that has already said no, and a retried 401 is how an account gets locked.

10. **A manifest names secrets and never carries them.** Manifests travel in
    source control and between environments. Any property whose name suggests
    a credential must end in `SecretName` or `Reference` — enforced by a
    reflection test, so a `Password` property holding a value cannot be added
    quietly.

## Consequences

### Accepted costs

- **The sync is always at least `SafetyLag` behind reality.** That latency is
  not a defect to be tuned away; it is what buys completeness. A connector
  needing sub-second freshness needs change-data-capture or a push feed, not a
  shorter lag.
- **Sources must expose a stable secondary sort key.** Some do not, or expose
  one that is not unique. Those sources cannot be synced gap-free by polling,
  and the honest answer is to say so rather than fall back to a timestamp-only
  cursor.
- **The composite cursor needs a matching index.** `(modified, id)` on the
  source side, or the query degrades to a scan on every pass.
- **Explicit `sourceIsFrozen` is friction**, and some callers will assert it
  falsely. It is still better than an implicit assumption, because a false
  assertion is a reviewable line in a manifest.

### What this does not claim

- **No transport is implemented.** There is no HTTP client, no OAuth flow, no
  connector host. `IConnector` and `IConnectorRuntime` from the phase text are
  not present. What is delivered is the logic that decides *what to read and
  when* — the part that is testable without a live source and the part where
  the silent data loss lives.
- **CDC is not implemented.** The phase names "watermark or CDC"; only the
  watermark path exists. CDC has genuinely different failure modes — log
  retention, snapshot-to-stream handover, schema evolution mid-stream — and
  sketching it would be worse than leaving it out.
- **Completeness is argued, not proven.** The composite cursor is gap-free
  *given* that the source orders by `(modified, id)` and applies the bounds it
  is given. The per-record re-check detects violations; it cannot prevent
  them, and a source that lies consistently would defeat it.
- **Schema mapping is not implemented.** Named in the phase; the Phase 05b
  metadata platform is the natural place for it and it is not wired up.

## Revisit triggers

- **A connector is configured with a lag shorter than its source's longest
  transaction.** The framework cannot detect this; if it starts happening,
  the lag needs to be derived from an observed transaction-duration
  distribution rather than declared.
- **A source without a stable secondary key must be integrated.** That needs
  its own decision about what guarantee is being given, and the answer is
  probably "at-least-once with downstream idempotency" rather than
  "gap-free".
- **`RecordsRejected` is routinely non-zero for a connector.** The source is
  not honouring its bounds, and every completeness claim about it is void.
- **CDC is added.** It is a different consistency model, not a faster
  watermark, and it needs an ADR rather than an extra enum value.
