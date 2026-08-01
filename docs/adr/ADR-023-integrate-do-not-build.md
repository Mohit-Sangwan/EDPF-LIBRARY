# ADR-023: Integrate, do not build (FHIR, HL7 v2, DICOM)

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Chief Architect, Clinical Informaticist, Sponsor

## Context

Phase 24 requires FHIR R4, HL7 v2.x and DICOM support. Each is a decades-old
standard with hundreds of pages of specification, a conformance test suite, and
a long tail of real-world deviation. This is the sharpest test of Principle 0
in the whole programme.

## Options considered

1. **Build each from scratch.** Total control over the object model and no
   third-party licence exposure. Cost: a FHIR engine is a multi-year programme
   in its own right and would consume this one. Applying the Principle 0
   test — *will we be better at this in three years than the leading library's
   maintainers, and does that advantage matter to a customer?* — gives no on
   both halves. A hospital does not buy EDPF because we wrote our own FHIR
   parser.
2. **Depend on the libraries directly**, exposing their types in EDPF's public
   API. Fastest, and it welds EDPF's public surface to a third party's
   versioning, so a Firely major bump becomes an EDPF breaking change.
3. **Integrate and wrap:** Firely .NET SDK for FHIR, NHapi for HL7 v2,
   fo-dicom for DICOM, each behind an EDPF abstraction.

## Decision

Option 3, which is Principle 0 applied literally: *integrate mature libraries
wherever they already solve the problem; wrap every third-party library behind
an EDPF abstraction so it can be replaced.*

- **FHIR:** Firely .NET SDK, behind `IFhirResourceMapper<T>`,
  `IFhirRepository`, `IFhirSearchTranslator`, `IFhirValidator`. FHIR search
  parameters translate onto the Phase 08 query builder rather than a second
  query path, so the injection guarantee (ADR-018) covers FHIR search too.
- **HL7 v2:** NHapi, behind message-mapping abstractions with **per-partner
  configuration**, because no two hospital interfaces are identical and
  pretending otherwise is the single most common integration failure.
- **DICOM:** fo-dicom, with pixel data streamed through the Phase 14 blob
  store rather than materialised.

**Licence review is part of the decision, not an afterthought** (ADR-009):
each library's licence is recorded, and each ships in its own optional package
under `verticals/` so the core graph stays clean for consumers who need none
of them.

## Consequences

- Positive: EDPF's effort lands on tenancy, audit, compliance and clinical
  safety — where nothing off the shelf helps — instead of re-implementing
  solved standards badly.
- Negative: EDPF inherits three dependency-update streams and their CVE
  surface. Accepted, and mitigated by the wrapping abstraction plus the
  SBOM/CVE gate.
- Accepted risk: a wrapped library's model may not express something a
  consumer needs. The abstraction is an escape hatch in both directions — the
  underlying client is reachable for advanced use, with the coupling explicit.

## What is *not* delegated

The libraries parse and serialise. They do not decide:

- **De-identification.** DICOM PS3.15 de-identification, including
  **burned-in-annotation detection**, is EDPF's own (Phase 20/24). This is the
  most commonly missed PHI leak in imaging, because the identifiers are pixels
  rather than metadata, and no parser will find them for you.
- **Clinical safety.** Unit conversion, reference ranges and dimension
  checking are EDPF's (`UnitConverter`), because "5 mg vs 5 mcg" is a safety
  property, not a formatting concern.
- **Terminology validity over time.** A code valid in 2019 may be retired in
  2026; historical records must still resolve. Effective-dated code systems
  are EDPF's responsibility.

## The regulatory boundary — stated plainly

**EDPF provides data infrastructure and does not make clinical decisions.**

A consumer who builds clinical decision support on EDPF is building a
**regulated medical device** (FDA Software as a Medical Device, EU MDR), with
all the obligations that carries. EDPF's documentation must say so plainly and
must never imply otherwise — no "clinical intelligence", no "decision
support", no phrasing that could be read as EDPF assessing a patient.

This is the same discipline as Golden Rule 5 (compliance is controls, not
claims) applied to medical-device regulation, and it is written here because
the boundary is easiest to blur in marketing copy, long after the engineering
decision was made correctly.

## Revisit trigger

A wrapped library becomes unmaintained or changes to an incompatible licence;
or a design partner needs behaviour the abstraction cannot express and the
underlying library cannot provide.
