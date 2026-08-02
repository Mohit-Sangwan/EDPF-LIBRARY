# Phase 26f — Enterprise Connector Framework

**Status:** Partial — sync correctness complete; transport and CDC deferred with reasons
**Gate contribution:** G6 (Integration)
**ADR produced:** [ADR-030 — Incremental sync is gap-free by construction](../adr/ADR-030-incremental-sync-completeness.md)

## What this phase delivers

> *"A uniform connector model … so each integration is configuration plus a
> thin adapter rather than a bespoke project."*

| File | Contents |
| --- | --- |
| [`SyncCursor.cs`](../../src/Edpf.Connectors/SyncCursor.cs) | The composite `(timestamp, id)` watermark |
| [`WatermarkPlanner.cs`](../../src/Edpf.Connectors/WatermarkPlanner.cs) | Safety-lag windows and per-record bound checks |
| [`Pagination.cs`](../../src/Edpf.Connectors/Pagination.cs) | Keyset, opaque-token, and offset-with-an-assertion |
| [`RetryPolicy.cs`](../../src/Edpf.Connectors/RetryPolicy.cs) | `Retry-After`, bounded backoff, deterministic jitter |
| [`ConnectorManifest.cs`](../../src/Edpf.Connectors/ConnectorManifest.cs) | Declared configuration and the connector-run audit record |

The value is not the HTTP plumbing — every team can write that. It is the
three defects every bespoke integration ships, which share one failure mode:
**the sync reports success and the data is incomplete.** Nothing throws,
nothing alerts, and the gap surfaces months later when someone reconciles
totals.

## Defect 1 — the timestamp-only watermark

Watermark `12:00:00`, three records stamped `12:00:00`. `modified > watermark`
loses all three permanently. `>=` re-reads the boundary forever.

The cursor is composite, so the comparison becomes
`(ts > last.Ts) OR (ts = last.Ts AND id > last.Id)` — gap-free *and*
duplicate-free. `AThousandRecordsAtOneInstant_AreEachReadExactlyOnce` walks a
cursor across a thousand records sharing a timestamp, asserts each is visited
once, then asserts a second pass reads nothing.

A cursor also cannot move backwards: `Advance` fails on a record at or before
the current position, turning an ordering bug into a visible failure rather
than a duplicate storm.

## Defect 2 — reading up to "now"

A transaction starts at `11:59:58`, stamps its row `11:59:58`, commits at
`12:00:03`. A sync at `12:00:00` reads to `12:00:00` and moves its watermark
there. The row becomes visible three seconds later carrying a timestamp
*behind* the watermark. **Not late: never.**

The upper bound is held back by a mandatory safety lag. `WatermarkPlanner`
**throws on a zero lag** rather than defaulting one, because a default gets
chosen once by whoever wrote the framework and never revisited by whoever
integrates a source with five-minute batch transactions. `ConservativeDefault`
is offered and documented as a starting point, not a guarantee.

The planner takes the **source's** clock. Using the reader's would reintroduce
skew as exactly the error the lag exists to absorb.

Bounds are re-checked per record, and rejections surface in the run record
rather than a log line — a non-zero count means the source is not honouring
the bounds it was given, which voids the completeness argument entirely.

## Defect 3 — offset pagination over a live set

Rows 0-99, then 100-199. A delete before position 50 shifts everything up one:
the row formerly at 100 is now at 99 and is never read.

Offset requires an explicit `sourceIsFrozen` assertion. A nightly extract
against a snapshot is real and safe, so it is permitted — it just has to be
*said*, because the silent default of "offset is fine" is how the skip
happens.

## Retry and secrets

- **`Retry-After` beats computed backoff, always, including above our own
  ceiling.** Our ceiling is a guess about the source's recovery; its header is
  a statement about it. Ignoring it turns a throttle into a ban.
- **Jitter is deterministic**, derived from attempt and a per-connector seed.
  Without jitter a fleet retries in lockstep and the synchronised herd is
  often what keeps a recovering source down. Deriving rather than drawing it
  keeps the schedule reproducible in a test and an incident review.
- **401, 403 and 400 are fatal.** Retrying adds load to a source that already
  said no, and a retried 401 is how an account gets locked.
- **A manifest names secrets and never carries them.** A reflection test
  requires any credential-ish property to end in `SecretName` or `Reference`.

That last test found its own bug: my first version forbade the *word*
"Credential" in a property name, which failed against `CredentialSecretName` —
the correct name for a pointer. The rule is not "no credential-ish names", it
is "anything credential-ish must be a reference". Fixing the assertion made it
express the actual invariant, and it now catches a `Password` property holding
a value while permitting a name that points at one.

## Exit criteria

| Criterion | Status |
| --- | --- |
| Incremental sync via watermark | Met — gap-free and duplicate-free |
| Pagination | Met — keyset, token, and offset behind an assertion |
| Rate-limit handling | Met — `Retry-After` honoured, bounded backoff, jitter |
| Error and retry semantics | Met — fatal failures separated from transient |
| Connector-level audit | Met — counts, window and cursor movement, no record content |
| Auth | **Partial** — declared and secret-referenced; no flow implemented |
| Incremental sync via CDC | **Deferred** |
| Schema mapping | **Deferred** |

## Deferred, with reasons

**Transport.** No HTTP client, no OAuth flow, no connector host. `IConnector`
and `IConnectorRuntime` from the phase text are not present. Driving a live
source cannot be verified without one, and per Z.12 a claimed capability is a
tested capability. What ships is the logic that decides *what to read and
when* — the part where the silent data loss actually lives.

**CDC.** The phase names "watermark or CDC"; only the watermark path exists.
CDC has genuinely different failure modes — log retention expiry,
snapshot-to-stream handover, schema evolution mid-stream — and it is a
different consistency model rather than a faster watermark. Sketching it would
be worse than leaving it out.

**Schema mapping.** Named in the phase. The Phase 05b metadata platform is the
natural home for it, and wiring the two together is real work rather than a
formality.

## What this does not claim

Completeness is **argued, not proven**. The composite cursor is gap-free
*given* that the source orders by `(modified, id)` and applies the bounds it is
given. The per-record re-check detects violations of that assumption; it cannot
prevent them, and a source that lies consistently would defeat it. ADR-030
records this rather than leaving it implied.
