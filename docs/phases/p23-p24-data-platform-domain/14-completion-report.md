# Wave 5 — Completion report (toward Gate G5: Domain)

**Phases:** p23–p24 · **Date:** 2026-08-01 · **Squads:** A + C + Clinical Informaticist

## What was built

The two capabilities from this wave that are both highest-value and provable
without a hospital integration environment: automated classification with
drift detection, and clinical unit safety. Produced
[ADR-023](../../adr/ADR-023-integrate-do-not-build.md).

| Phase | Delivered | Location |
|---|---|---|
| **23** | `IdentifierValidators` (Luhn, NHS mod-11, Aadhaar Verhoeff, SSN structural), `DataClassifier` with drift detection | `src/Edpf.DataPlatform/Classification/` |
| **24** | `UnitConverter` with dimension checking, `ReferenceRange` with critical bounds | `src/Edpf.DataPlatform/Clinical/` |
| **24** | ADR-023 integrate-don't-build, and the SaMD regulatory boundary | `docs/adr/ADR-023-integrate-do-not-build.md` |

## Verification

**693 automated tests green** — 613 unit, 47 isolation, 18 architecture, 12
walking-skeleton component, 3 conformance contract.

## Two properties worth stating

**Classification drift is detectable, and precision makes it actionable.**
Phase 01 made classification declarative so encryption, redaction, audit and
export controls all follow from an attribute — which means a developer who
adds an unmarked `PatientNotes` column silently opts it out of every one of
them. The classifier catches exactly that, and it buys precision with check
digits: a sixteen-digit order number is **not** reported as a payment card,
because a classifier that cries wolf is muted within a week and a muted
classifier detects nothing. Findings carry field and kind but never the value,
so the drift report does not become the leak it was written to prevent.

**Unit confusion fails closed.** 5 mg administered as 5 mcg is a
thousand-fold error and both look plausible on a screen. Conversion across
dimensions is **refused** — mass to volume needs a density, which is a
question about a substance, not arithmetic. Unknown units are refused rather
than assumed, because assuming is how a typo becomes a dose. Comparison is
case-sensitive because UCUM is: `mg` and `Mg` differ by nine orders of
magnitude. Arithmetic is `decimal` throughout, since a binary rounding
artefact in a dose calculation is not acceptable. And an inverted reference
range is rejected at construction, because it would silently classify every
result — including every critical one — as normal.

## ADR-023 and the regulatory boundary

The sharpest Principle 0 test in the programme: FHIR, HL7 v2 and DICOM are
integrated (Firely, NHapi, fo-dicom) and wrapped, never rebuilt. A FHIR engine
is a multi-year programme that would consume this one, and no hospital buys
EDPF because we wrote our own parser.

What is explicitly **not** delegated: DICOM PS3.15 de-identification including
burned-in-annotation detection (the identifiers are pixels, and no parser will
find them for you), clinical safety, and terminology validity over time.

The ADR also records the boundary plainly: **EDPF provides data infrastructure
and does not make clinical decisions.** A consumer building decision support on
EDPF is building a regulated medical device (FDA SaMD / EU MDR). That is
written down because it is easiest to blur in marketing copy, long after the
engineering decision was made correctly.

## An ADR-002 decision rather than an accident

`[GeneratedRegex]` requires .NET 7+, which forced a choice about
`Edpf.DataPlatform`'s target frameworks. Rather than reach for whichever API
happened to compile, the assembly now targets **Tier 1 + Tier 2 only**, via a
new `EdpfPlatformTargetFrameworks` property — because ADR-002 defines the
Tier 3 surface as "data access, config, logging, security primitives", and
data-platform services are not in it. A net48 host consumes the platform
through its API rather than in-process. The regex implementation then uses
compiled `Regex` so Tier 2 still works.

That is the difference between a tiering decision and a build error worked
around, and the property name is where the reasoning lives.

## Carried forward to Gate G5

| Requirement | Phase | Why not now |
|---|---|---|
| CDC ordering and resumability under process kill | 23 | Needs live engines with CDC enabled |
| Lineage accuracy across a multi-hop pipeline | 23 | Needs the pipeline |
| Entity-resolution precision/recall vs. a labelled match set | 23 | The labelled set is a data asset that must be built with clinical input; an over-eager patient merge is a safety incident, so this cannot be self-scored |
| Projection rebuild correctness | 23 | Needs the projection store |
| Synthetic-data statistical fidelity and re-identification resistance | 23 | Needs a reference population |
| Warehouse/lakehouse sinks | 23 | Needs the target platforms |
| **FHIR round-trip against a public test server** | 24 | Gate G5 criterion; needs Touchstone and the Firely integration |
| HL7 and DICOM interop | 24 | Needs NHapi/fo-dicom and a message corpus |
| DICOM de-identification incl. **OCR of pixel data** | 24 | Needs imaging data and an OCR pipeline |
| Terminology mapping vs. reference sets | 24 | Needs licensed SNOMED CT / LOINC / RxNorm content |

**Gate G5 is NOT passed.** Its headline criterion — FHIR round-trip against a
public test server — is by definition an integration test.

One item deserves its own note: **entity-resolution accuracy cannot honestly
be self-scored.** Merging two records for the same patient is a clinical-safety
operation, and precision/recall against a labelled match set requires that
labelled set to be built with clinical input. Reporting a number derived from
my own fixtures would be worse than reporting none.

## Programme position

Five waves in, 693 tests, 23 ADRs. The pattern has been consistent: contracts
plus the safety properties that cannot be retrofitted, verified by test;
infrastructure-, reviewer- and data-bound criteria deferred with reasons. What
is complete is the part that decides whether the rest is buildable safely, and
every deferred item is scheduled work against fixed contracts rather than open
design.
