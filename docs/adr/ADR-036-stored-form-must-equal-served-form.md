# ADR-036 — What is stored must equal what was hashed, and what is replayed must equal what was served

- **Status:** Accepted
- **Date:** 2026-08-03
- **Phase:** 02 / 09 (revisited) — found by running the Tier A parity suite for the first time
- **Related:** [ADR-008](ADR-008-test-strategy.md) (two Tier A providers, identical suite), [ADR-019](ADR-019-idempotency-contract.md), [ADR-022](ADR-022-audit-event-taxonomy.md), [ADR-032](ADR-032-migration-verification.md)

## Context

The Docker-gated provider-parity suite — the identical gate demonstrations run
against SQL Server *and* PostgreSQL via Testcontainers — had never been
executed. It was excluded by `Category!=RequiresDocker` on every run.

Running it produced **three failures, and two distinct defects**. Both are the
same mistake in different clothes: **a value was written in one form and read
back in another, and something depended on the two being equal.**

### Defect 1 — the audit chain cannot verify on PostgreSQL

`AuditWriter.ToCanonicalBytes` hashes `row.OccurredUtc.UtcTicks` — 100 ns
resolution. Confirmed directly against `postgres:16-alpine`:

```
'2026-08-02 12:00:00.1234567+00'::timestamptz  →  2026-08-02 12:00:00.123457+00
```

PostgreSQL keeps microseconds and **rounds**. SQL Server `datetime2(7)` keeps
100 ns and round-trips exactly. So the hash computed before the write differs
from the hash recomputed after the read — on one Tier A provider only, every
time, and **the failure is indistinguishable from tampering**.

This is the tamper-evidence mechanism the entire compliance story rests on,
and it did not work on half the supported providers.

### Defect 2 — an idempotent replay was not the response it replayed

`IdempotencyFilter` stored the response body via
`JsonSerializer.Serialize(value)` — whose defaults are **PascalCase**. Minimal
APIs serve the original with `JsonSerializerDefaults.Web` — **camelCase**.

So the first call returned `{"id":…}` and the replay returned `{"Id":…}`. Same
data, different contract. A client that parsed the original **breaks on the
retry**, which is precisely the case idempotency exists to make safe.

This one is provider-independent — it failed identically on both — and it had
been passing the PowerShell gate demonstration all along, because
`Invoke-RestMethod` yields objects with case-insensitive property access.
**The script's leniency hid it; the strict test found it.**

## Decision

**A value's stored form must be identical to the form that was hashed, and a
replayed response must be identical to the response it replays.**

1. **Instants are normalised to microsecond precision before they are hashed
   or stored.** `StorableInstant.Normalize` rounds — matching PostgreSQL's own
   rule rather than truncating, because truncating would disagree with a store
   that rounds and reproduce the same defect one digit lower.

2. **Normalisation happens at construction, not at comparison.** The row
   carries a storable instant from the moment it exists, so the hash covers
   exactly what the database will hold. Fixing this at verification time —
   re-rounding before recomputing — would leave the stored hash covering a
   value that no longer exists anywhere.

3. **A replayed body is serialized with the same options the original response
   used.** Byte-identical, or it is not a replay.

4. **Microsecond is the floor because it is the coarsest resolution among
   supported stores** — SQL Server 100 ns, PostgreSQL 1 µs, MySQL
   `DATETIME(6)` 1 µs. A store coarser than that would need this revisited,
   not worked around.

## Consequences

### Accepted costs

- **Audit timestamps lose sub-microsecond resolution.** No audit requirement
  anywhere asks for it, and ordering is carried by `Sequence` rather than by
  time, so nothing depends on the discarded digit.
- **Existing chains written before this change do not verify.** In this
  repository that is only ephemeral test data. A deployment holding real
  chains would need a documented re-anchoring, which is a migration rather
  than a code change.
- **`StorableInstant` is a rule people must remember to apply.** It is applied
  at the one place that hashes an instant today; nothing prevents a future
  hash input from forgetting it. `IsStorable` exists so an assertion is cheap,
  but there is no enforcement.

### What this does not claim

- **The parity suite covers the walking skeleton, not the framework.** It is
  the only place with concrete provider-backed implementations, so it is the
  only place these defects could surface. The same classes of defect are
  possible anywhere the framework later persists a hashed value.
- **Two Tier A providers is not thirteen.** ADR-008's full matrix remains
  unrun; MySQL, Oracle and the rest could each round differently again.

## Revisit triggers

- **A store with resolution coarser than a microsecond enters the matrix.**
  The floor moves and every existing chain is affected.
- **Any new hash input includes a value the store may round or reformat** —
  a decimal with scale, a string the collation may fold, a JSON column the
  engine may reorder. This ADR's rule applies; the mechanism does not
  self-extend.
- **A second serializer configuration appears on a response path.** The replay
  must follow it, and nothing currently checks that it does.
