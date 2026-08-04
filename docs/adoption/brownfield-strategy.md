# Brownfield adoption — moving an existing system onto EDPF

**Audience:** an engineering lead with a working HIS, ERP or LIS who has been
asked whether EDPF is worth adopting.

**The short answer to "how do we adopt this?":** one bounded context at a time,
never all at once, with the legacy system still serving until a reconciliation
proves per-record equivalence. The framework enforces that sequence rather than
recommending it — see [`CutoverPlan`](../../src/Edpf.Migration/Cutover.cs).

---

## Why this document exists

The most common way an enterprise framework dies is not technical. It is that
the framework is excellent, the existing applications already work, and nobody
can articulate a first step that is smaller than "rewrite it". Two years later
the framework has three sample applications and no production users.

That failure has a name in this repository's own risk register — *"nobody
migrates off legacy"* — and it is recorded as critical. This document is the
answer to it.

**It is deliberately not a sales pitch.** The most useful section is
[the one that tells you not to adopt](#when-not-to-adopt).

---

## The unit of adoption is a bounded context, not an application

Do not migrate "the HIS". Migrate *scheduling*, or *specimen tracking*, or
*billing*. Each is a bounded context with its own data, its own users, and —
critically — its own definition of done.

A bounded context is the right unit because it is the smallest thing that can
be cut over independently and reversed independently. Anything smaller shares a
transaction with the code you left behind; anything larger cannot be reversed
without reversing work that was going fine.

**Pick the first one on these criteria, in this order:**

| Criterion | Why it dominates |
|---|---|
| Low clinical risk if it degrades | The first migration is where you learn the process. Learn it on appointment reminders, not on medication administration |
| Clear data ownership | If two legacy modules both write the table, you are migrating two contexts and calling it one |
| A real compliance pain today | Adoption needs a reason beyond "the new framework". "Our audit trail is not tamper-evident and the auditor said so" is a reason |
| Small enough to finish in one quarter | A migration that outlives the team's attention span is reversed by attrition, not by decision |

Do **not** pick the context with the worst code. It is tempting, and it is the
one where legacy behaviour is least understood, which is exactly what
reconciliation needs you to understand.

---

## The five stages, and which of them you can undo

`CutoverStage` is an ordered enum, and `CutoverPlan.Advance` refuses to skip.
The value of naming the stages is that **"can we still go back?" has an answer
somebody can look up at three in the morning** rather than reconstruct during an
incident.

| Stage | Who serves reads | Who takes writes | Reversible? |
|---|---|---|---|
| `LegacyOnly` | Legacy | Legacy | — nothing has happened |
| `Backfilled` | Legacy | Legacy | ✅ delete the copied data |
| `DualWrite` | Legacy | **Both** | ✅ stop writing to EDPF |
| `NewSystemReads` | **EDPF** | Both | ✅ a configuration change |
| `LegacyRetired` | EDPF | EDPF | 🚫 **restore from backup and lose everything since** |

Two things about that table are enforced in code rather than documented:

**Advancing from `DualWrite` onward requires a `ReconciliationReport`.**
Not a row count — [`Reconciler`](../../src/Edpf.Migration/Reconciliation.cs)
compares per record by fingerprint, with canonicalisation declared field by
field and a written justification for each. A reconciler that has been
configured to compare nothing is refused at construction. This exists because a
migration signed off on matching row counts is the standard way two datasets
with swapped values pass review.

**Retiring legacy is a separate method.** `RetireLegacy` is not `Advance` with a
flag; it takes a typed acknowledgement and is reachable only from
`NewSystemReads`. Every other stage transition is `Advance`/`Reverse`. That
asymmetry is the point: the irreversible step should not be one keystroke away
from the reversible ones.

---

## Coexistence: who owns the write

The hardest question in dual-write is not "how do we write twice". It is **what
happens when the second write fails.**

EDPF's answer is the same as everywhere else in the framework: the transactional
outbox (ADR-003). During `DualWrite`, the legacy system remains the source of
truth and EDPF's write rides an outbox message committed in the same local
transaction as the legacy change. If the EDPF write fails, the outbox retries
it; if it fails permanently, the divergence is *visible in reconciliation*
rather than silently absent.

**Do not implement dual-write as two synchronous calls in application code.**
That produces a distributed transaction with no coordinator, and its failure
mode — legacy written, EDPF not, nobody notified — is the exact case dual-write
exists to detect.

### The overlap period is where you learn whether it worked

Run `DualWrite` long enough to cover your slowest real cycle. For most clinical
contexts that is a month-end, not a week. Reconcile continuously, not once at
the end: a divergence found on day 3 is a bug, and the same divergence found on
day 60 is sixty days of data to repair.

---

## What you do not have to migrate

Three things adopting teams routinely try to move and should not:

- **Historic audit trails.** EDPF's audit chain is tamper-evident from its own
  genesis record. Importing legacy audit rows into it produces a chain that
  verifies over data whose integrity was never protected, which is a stronger
  claim than the truth supports. Keep the legacy audit store, read-only, for its
  retention period. Two audit stores with honest boundaries beat one with a
  fabricated one.
- **Every historic record.** Backfill what the business actually reads. A
  ten-year archive nobody queries can stay where it is behind a read-only view;
  moving it multiplies reconciliation work by the size of the archive and
  reduces risk by nothing.
- **Legacy identifiers.** Keep them, in a dedicated correlation field. Do not
  reuse them as EDPF primary keys. Legacy key schemes carry semantics — ward
  prefixes, year-of-issue segments — and inheriting them means inheriting every
  constraint that produced them.

---

## Environment and data strategy

The one rule that is not negotiable: **production data does not go to non-production
environments.** In a clinical system this is not hygiene, it is the difference
between a test environment and an unreported breach.

Use the de-identification path (`SafeHarborDeidentifier`) to produce test data,
and note what it costs you: Safe Harbor removes the identifiers, which means
your test data will not reproduce identity-matching defects. Test those against
synthetic records with deliberately constructed collisions instead.

| Environment | Data | Refresh |
|---|---|---|
| Dev | Synthetic only | On demand |
| Test | Synthetic + de-identified extract | Per sprint |
| Staging | De-identified, production-shaped volume | Before each cutover stage |
| Production | Production | — |

Staging matters more during a migration than at any other time: it is the only
place a `DualWrite` reconciliation runs at production volume before you rely on
it.

---

## What EDPF gives you that assembling the parts does not

A fair question from anyone who could assemble EF Core, Marten, Firely and
Hangfire directly. The honest answer is that the data access is *not* the
argument — ADR-001 says as much, and the data layer is deliberately the
thinnest viable wrapper over libraries other people maintain.

The argument is that the following are **structural rather than disciplinary**,
and stay true across every bounded context you migrate, including the ones
written by the team that joins next year:

- A query cannot omit the tenant predicate; an unresolved tenant is refused,
  never read as "all tenants".
- A cross-tenant read is indistinguishable from a missing record.
- Classified data cannot reach a log, an export, an SMS or a model prompt
  without an explicit, audited decision — one classification table drives all
  of them.
- Erasure and audit coexist: destroying a subject's key makes their data
  unrecoverable while the chain still verifies.
- A migration cannot be signed off on a row count.

Assembling the parts gives you all the capability and none of those properties.
You can build them yourself; that is the actual comparison, and it is a fair
one to make deliberately.

---

## When not to adopt

Stated plainly, because a document that cannot say this is marketing:

- **You have one application, one tenant, and no regulatory obligation.** Nearly
  everything above is overhead for you. Use EF Core.
- **Your constraint is a runtime EDPF does not support.** .NET 6 was retired
  ([ADR-038](../adr/ADR-038-retire-tier-2.md)); Tier 3 (net472/net48) gets a
  reduced surface — data access, config, logging, security primitives — and not
  the platform assemblies.
- **You need a capability that is deferred.** Vector search, real-time push and
  time-series are named in [ADR-037](../adr/ADR-037-v1-scope-boundary.md) with
  their triggers. Check the list before assuming.
- **You cannot staff the overlap.** Dual-write plus reconciliation is real work
  for a real quarter. A migration nobody is rostered for stalls at
  `Backfilled`, and a stalled migration is worse than none: you now maintain two
  systems and the reconciliation between them.

---

## The first ninety days, concretely

1. **Week 1–2.** Pick the bounded context against the criteria above. Write
   down what "equivalent" means for its records — that sentence becomes the
   `Reconciler`'s canonicalisation configuration, and every relaxation of it
   needs a written justification.
2. **Week 3–6.** Build the context on EDPF against synthetic data. Do not
   connect it to anything. This is where you find out whether the framework fits
   your domain, at a cost of six weeks rather than a programme.
3. **Week 7–8.** Backfill into staging at production volume. Run the reconciler.
   Expect it to fail the first time; that failure is the deliverable.
4. **Week 9–12.** `DualWrite` in production, legacy still authoritative,
   reconciling daily. Advance no further until a full business cycle is clean.

`NewSystemReads` and `RetireLegacy` are the following quarter's decisions, taken
on evidence the reconciler produced rather than on confidence.

---

## Related

- [ADR-032](../adr/ADR-032-migration-verification.md) — equivalence is proven per
  record, canonicalisation declared
- [ADR-021](../adr/ADR-021-zero-downtime-migration.md) — expand–migrate–contract
  within a schema
- [ADR-003](../adr/ADR-003-consistency-model.md) — the outbox dual-write relies on
- [Shared-responsibility model](../compliance/shared-responsibility-model.md) —
  what compliance you inherit and what stays yours
