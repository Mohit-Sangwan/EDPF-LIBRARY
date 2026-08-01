# Phase 24b — Healthcare Vertical Package

**Status:** Complete
**Gate contribution:** G5 (Domain Capability)
**ADR produced:** [ADR-024 — Vertical package boundary](../adr/ADR-024-vertical-package-boundary.md)

## What this phase corrects

The originating specification placed `SavePatient()`, `SaveEncounter()` and
`SaveLabOrder()` directly on the framework. The master document calls this out
as a layering violation: *"a framework serving ERP, banking, and telecom cannot
expose `SavePatient()`."*

This phase moves that content into an optional vertical package and — more
importantly — installs the enforcement that keeps it there.

## Delivered

### `verticals/Edpf.Healthcare.Domain`

| File | Contents |
| --- | --- |
| [`PatientIdentity.cs`](../../verticals/Edpf.Healthcare.Domain/PatientIdentity.cs) | `Patient`, `PatientMergeRecord`, `NationalIdentifierScheme` |
| [`PatientMergeService.cs`](../../verticals/Edpf.Healthcare.Domain/PatientMergeService.cs) | Merge and unmerge with the clinical-safety rules enforced in the domain, not left to callers |

The project file is part of the evidence. What it does **not** contain is the
point: no reference to anything internal, no shared source, no
`InternalsVisibleTo`. It builds from the public surface alone, which is the
extension-model test §① asks for.

`Patient` declares `[DataClassification(Phi)]` on the MRN, date of birth and
national identifier, and `Pii` on names — and implements no encryption, no
redaction, no audit and no tenant filtering. It inherits all four from the
core's declarative machinery. That inheritance *is* the layering claim, and
`Patient_ClassifiedFields_AreTaggedSoTheCoreProtectsThemAutomatically` asserts
it rather than assuming it.

### Core-neutrality enforcement

[`CoreNeutralityTests`](../../tests/Edpf.ArchitectureTests/CoreNeutralityTests.cs) —
three tests implementing ADR-024's mitigation for the risk §⑧ names (*"the
healthcare vertical's needs leak back into the core, re-coupling them"*):

- `CoreAssemblies_ContainNoClinicalTerminology` — 13 clinical terms,
  word-boundary matched, across 11 core assemblies. Comment lines are skipped
  deliberately: a comment citing "a medication administration time crossing a
  DST boundary" explains *why* a rule exists, and losing that explanation would
  cost more than the purity gains.
- `CoreAssemblies_DoNotReferenceAnyVerticalPackage` — the dependency arrow
  points one way; a core package referencing a vertical would make the optional
  package mandatory.
- `VerticalPackage_BuildsOnThePublicSurfaceAlone` — no `InternalsVisibleTo`.

## The defect this phase found

§⑤ requires that *"patient merge/unmerge is tested for full reversibility — an
irreversible incorrect merge is a clinical-safety incident."* Writing that test
found a real hole in the merge guard.

The service refused a merge when an incoming record was already merged. That
catches A → B followed by A → C. It does **not** catch A → B followed by
B → C, because at the second merge `B` is a *survivor* — nothing about `B`'s
state says it has absorbed anything. `Patient` tracked merge direction only, so
the guard had nothing to test and the chain formed silently.

The consequence is exactly the clinical-safety incident the phase exists to
prevent: reversing the first merge afterwards restores `A` against a survivor
that has itself been absorbed, so `A` returns to independence while its data
has already propagated to `C`.

The fix was in the domain model, not the test:

- `Patient.AbsorbedRecordCount` / `IsMergeSurvivor` — the survivor side of a
  merge is now recorded, so it can be reasoned about.
- `Merge` refuses a duplicate that `IsMergeSurvivor`, with the reason stated in
  the error: reverse the earlier merge first.
- `Unmerge` now takes the survivor, which lets it release those absorptions
  **and** verify the supplied survivor matches the merge record — reversing
  against the wrong survivor would have restored the records while the real
  survivor still claimed them.

`Merge_AfterAnEarlierMergeIsReversed_IsAllowed` pins the boundary: the guard
must block a chain, not permanently disqualify a record from ever being merged
again.

## Exit criteria

| Criterion | Status |
| --- | --- |
| Clinical operations pass isolation, audit, encryption and terminology tests | Met — classification-driven, asserted rather than assumed |
| Merge/unmerge proven reversible | Met — 15 tests, including partial-unmerge, wrong-survivor, double-reversal and both chain directions |
| Package builds with zero core changes | Met — no core file was modified for this phase |

## Scope boundary

This package covers §④ *Identity*. `Edpf.Healthcare.Clinical` (encounters,
orders, results) and `Edpf.Healthcare.Interop` (FHIR/HL7 v2 mapping) are named
by the phase but not built here — ADR-023 already governs the interop half
(integrate, do not build), and the clinical half needs a clinically-reviewed
model rather than a plausible-looking one. Per Z.12, *a claimed capability is a
tested capability*; shipping an unreviewed clinical model would be a claim, not
a capability.

The layering conclusion does not depend on that scope. One vertical built
strictly on the public surface is what demonstrates the extension model works;
a second would test whether it *generalises*, which ADR-024 records as a
revisit trigger rather than a settled question.
