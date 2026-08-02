# ADR-032 — Migration equivalence is proven per record, with canonicalisation declared field by field

- **Status:** Accepted
- **Date:** 2026-08-02
- **Phase:** 35b — Brownfield Migration Kit
- **Closes:** risk register item 3, *"Nobody migrates off legacy"* (Critical)
- **Related:** [ADR-028](ADR-028-profiling-discloses-nothing.md) (reports that travel), [ADR-021](ADR-021-zero-downtime-migration.md) (schema migration), [ADR-030](ADR-030-incremental-sync-completeness.md)

## Context

The risk register rates *"nobody migrates off legacy"* as **critical**, and a
framework nobody can move onto is a framework nobody adopts.

The blocker is rarely the new system's capability. It is that **somebody has to
be accountable for the claim that the new system holds the same data as the old
one**, and there is usually no way to substantiate that claim. So the migration
stalls at 90%, the legacy system stays up "just in case", and both are
maintained forever.

Three things make the claim hard.

**Row counts prove nothing.** Two datasets with identical counts can have every
value swapped between rows. Count, sum, min and max all agree. Equivalence is a
per-record property and nothing cheaper establishes it.

**Comparing values means holding them twice.** A reconciliation job that reads
both copies, and a report that quotes differing values, is a second
uncontrolled copy of the data — travelling by email and ticket exactly as
ADR-028 describes for quality profiles.

**Representations differ legitimately.** Legacy stores `1.50`, the new system
stores `1.5`. Legacy pads with spaces. Legacy stores `""` where the new system
stores `NULL`. Some of these are noise and some are defects, and **which is
which is a per-field judgement nobody can make automatically.**

## Decision

**1. Equivalence is established per record, by fingerprint.**
A digest over the canonicalised compared fields, computed independently on each
side and compared. The digest is length-prefixed per field, so `"ab"+"c"`
cannot collide with `"a"+"bc"` — a surname shifting one character into a given
name must not reconcile cleanly. Fields are sorted by name before digesting, so
the same record fingerprints identically on any machine.

The digest uses FNV-1a rather than `string.GetHashCode()`, which is
deliberately randomised per process and would make every record differ between
two runs.

**2. The fingerprint is not a security control.** It detects accidental
divergence, which is what migrations suffer from. An adversary who chooses
values can construct collisions in any non-cryptographic digest, and nothing
here defends against that.

**3. The report names keys, never values.** A difference nobody can locate is a
difference nobody can fix, so the key is carried. Values are not — the report
must not become the second copy of the data.

**4. Canonicalisation is declared per field and every relaxation carries a
written justification.** `Exact` is the default because it is the option that
cannot hide a defect. Anything else throws without a reason string.

Normalise too little and the report floods with false differences; a report
that is 99% noise is one nobody reads, so the 1% that matters is never seen.
Normalise too much and **the differences you normalise away are exactly the
ones you will never find again** — trimming everywhere hides a legacy system
that silently truncated; case-folding everywhere hides a mangled surname.

**5. Ignored fields are reported prominently, and a reconciler that compares
nothing is refused at construction.** What a reconciliation did *not* check is
the first thing an auditor asks about, and a clean report over three fields out
of forty is not evidence. A reconciler with every field ignored would report
any two datasets as equivalent — a clean report that means nothing, signed off
by someone who believed it.

**6. `NULL` and `""` are different.** A legacy system storing an empty string
where the new one stores null is a real difference that changes every
downstream nullable check.

**7. Cutover stages are explicit, advance one at a time, and retiring legacy is
a separate act requiring a typed acknowledgement.** Skipping from `Backfilled`
to serving reads means the new system has never been observed to stay in step
under live write traffic, which is the only thing dual-write is for. And an
irreversible step that looks identical to a reversible one will eventually be
taken by someone who thought it was reversible.

## Consequences

### Accepted costs

- **Declaring comparison rules is work**, field by field, with justifications.
  For a forty-column table that is forty decisions. It is also the only
  artefact that makes the eventual sign-off meaningful.
- **The typed acknowledgement is friction by design**, and someone will
  copy-paste it. The value is that the step is *distinguishable*, not that it
  is hard.
- **Fingerprint comparison needs both datasets enumerable by key.** A source
  that cannot be read in key order, or has no stable business key, cannot be
  reconciled this way.

### What this does not claim

- **No transformation engine.** This package verifies migrations; it does not
  perform them. Mapping legacy shapes to new ones is bespoke by nature and
  pretending otherwise would produce a generic mapper nobody's schema fits.
- **No dual-write transport.** `CutoverStage.DualWrite` names the stage and the
  reconciler measures its divergence; the mechanism that writes to both systems
  is the adopter's, and its failure semantics (legacy succeeds, new fails)
  belong to ADR-003's outbox and saga model.
- **No backfill runner.** Chunking, resumption and throttling against a live
  production source are real work with real infrastructure dependencies, and a
  runner never pointed at a loaded database is a claim rather than a capability
  (Z.12).
- **The fingerprint proves equivalence under the declared rules only.** It is
  exactly as strong as the comparison rules someone wrote, which is why the
  ignored-field list is part of the report rather than a footnote.

## Revisit triggers

- **A team relaxes a comparison rule to make a report clean.** The
  justification string makes this visible in review; if it starts happening
  routinely, the rules are being fitted to the answer.
- **Collision resistance is needed** — a hostile migration, or a regulator who
  wants a cryptographic attestation. Then the digest becomes SHA-256 and the
  cost is measured rather than assumed.
- **A source without a stable business key must be migrated.** Key-based
  reconciliation does not apply, and whatever replaces it needs its own
  decision about what is being guaranteed.
- **Anyone proposes retiring legacy from a stage other than `NewSystemReads`.**
  That is the sequence's whole load-bearing constraint.
