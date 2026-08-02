# Phase 35b — Brownfield Migration Kit

**Status:** Verification and cutover complete; transformation, dual-write transport and backfill runner deferred
**Gate contribution:** G8 (Adoptable) · closes risk-register item 3
**ADR produced:** [ADR-032 — Migration equivalence is proven per record](../adr/ADR-032-migration-verification.md)

## Why this phase exists

The risk register rates *"nobody migrates off legacy"* as **critical**. A
framework nobody can move onto is a framework nobody adopts.

The blocker is rarely the new system's capability. It is that **somebody has to
be accountable for the claim that the new system holds the same data as the old
one** — and there is usually no way to substantiate it. So the migration stalls
at 90%, the legacy system stays up "just in case", and both are maintained
forever.

So this kit is built around verification rather than transformation.

| File | Contents |
| --- | --- |
| [`RecordFingerprint.cs`](../../src/Edpf.Migration/RecordFingerprint.cs) | Per-record digest with declared canonicalisation |
| [`Reconciliation.cs`](../../src/Edpf.Migration/Reconciliation.cs) | Equivalence proof that names keys, never values |
| [`Cutover.cs`](../../src/Edpf.Migration/Cutover.cs) | Strangler-fig stages with an explicit point of no return |

## Row counts prove nothing

Two datasets with identical counts can have every value swapped between rows.
Count, sum, min and max all agree.
`DatasetsWithEqualCountsButSwappedValues_AreNotEquivalent` is the test that
states it.

Extra rows matter as much as missing ones — a migration that duplicated a batch
produces a target containing everything the source has, and a check that only
looks for absences passes it.

## The report must not become a second copy of the data

A reconciliation report is emailed, ticketed and archived exactly like the
quality profiles ADR-028 governs. So differences carry the **key** — a
difference nobody can locate is a difference nobody can fix — and never the
values. `DifferencesNameTheKey_ButNeverTheValues` asserts that no value appears
anywhere in the rendered report.

Differences are sorted, so two runs produce a diffable report. One that
reorders between runs cannot be used to show yesterday's differences were
fixed.

## Canonicalisation is a judgement, so it is declared

Legacy stores `1.50`, the new system stores `1.5`. Legacy pads with spaces.
Legacy stores `""` where the new system stores `NULL`. Some of these are noise
and some are defects, and **which is which is a per-field judgement nobody can
make automatically.**

- Normalise too little → the report floods with false differences. A report
  that is 99% noise is one nobody reads, so the 1% that matters is never seen.
- Normalise too much → **the differences you normalise away are exactly the
  ones you will never find again.** Trimming everywhere hides a legacy system
  that silently truncated a field; case-folding everywhere hides a mangled
  surname.

`Exact` is therefore the default, and every other option **throws without a
written justification**. A reconciler with every field ignored is refused at
construction: it would report any two datasets as equivalent, which is the most
dangerous possible output — a clean report that means nothing, signed off by
someone who believed it.

## The defect the tests caught

`NumericCanonicalization_TreatsOnePointFiveZeroAsOnePointFive` failed on first
run. **`decimal` preserves scale**: `1.50m == 1.5m` is `true` as decimals, but
`1.50m.ToString()` is `"1.50"` and `1.5m.ToString()` is `"1.5"` — and the
fingerprint compares text, not decimals.

So the numeric canonicalisation parsed correctly and still produced two
different canonical forms, meaning every fixed-point legacy column would have
reconciled as fully different. Fixed by formatting with `#` placeholders to
strip trailing zeros.

Worth noting *why* it was caught: the test was written from the stated intent
("1.50 and 1.5 are the same number") rather than from what the implementation
produced.

## Cutover: reversibility is explicit

Five named stages, advancing one at a time. Skipping from `Backfilled` to
serving reads means the new system has never been observed to stay in step
under live write traffic — the only thing dual-write is for.

**Retiring legacy is a separate method taking a typed acknowledgement**, not
one more `Advance`. An irreversible step that looks identical to a reversible
one will eventually be taken by someone who thought it was reversible. Once
taken, `Reverse` refuses and *says why*: reversing after legacy stops being
written is a restore-from-backup, not a rollback, and a half-succeeding
reversal leaves two partial systems and no source of truth.

`IsReversible` exists so "can we still go back?" has an answer somebody can
look up at three in the morning rather than reconstruct during an incident.

## Exit criteria

| Criterion | Status |
| --- | --- |
| Migration verification tooling | Met |
| Patterns — strangler-fig staging with reversibility | Met |
| Reconciliation that does not disclose data | Met |
| Cutover with explicit point of no return | Met |
| Worked example migrating a real legacy module | **Deferred** |
| Transformation engine, dual-write transport, backfill runner | **Deferred** |

## Deferred, with reasons

**Worked example.** The phase asks for one migrating a real legacy module.
There is no real legacy module here to migrate, and a fabricated one would
demonstrate that the kit works against data shaped to suit it. This is a
genuine gap and it needs a design partner rather than more code — which is
already item 3 on the sponsor's list in
[PROGRAMME-STATUS](../PROGRAMME-STATUS.md).

**Transformation engine.** This package verifies migrations; it does not
perform them. Mapping legacy shapes to new ones is bespoke by nature, and a
generic mapper is one nobody's schema quite fits.

**Dual-write transport.** The stage is named and its divergence is measurable,
but the mechanism writing to both systems is the adopter's. Its failure
semantics — legacy succeeds, new fails — belong to ADR-003's outbox and saga
model rather than being reinvented here.

**Backfill runner.** Chunking, resumption and throttling against a live
production source are real work with real infrastructure dependencies. A runner
never pointed at a loaded database is a claim, not a capability (Z.12).
