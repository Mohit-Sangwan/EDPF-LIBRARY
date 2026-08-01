# Wave 4 — Completion report (toward Gate G4: Trust)

**Phases:** p18–p22 · **Date:** 2026-08-01 · **Squad:** B

## What was built

The phases that make EDPF usable in a hospital: the error taxonomy and its
security property, de-identification, hierarchical authorization, and the
consent and legal-hold contracts. Produced
[ADR-022](../../adr/ADR-022-audit-event-taxonomy.md).

| Phase | Delivered | Location |
|---|---|---|
| **18** | `EdpfException` taxonomy, eleven typed exceptions carrying catalogue errors | `src/Edpf.Abstractions/Errors/` |
| **20** | `IDeidentifier`, the eighteen Safe Harbor identifiers as an enum, `SafeHarborDeidentifier` with per-subject date shifting | `src/Edpf.Abstractions/Security/`, `src/Edpf.Security/` |
| **21** | `AuthorizationScope` (org → facility → department → unit → resource), `AuthorizationDecision` | `src/Edpf.Abstractions/Identity/` |
| **19/22** | `IConsentEvaluator`, `LawfulBasis`, `ProcessingRequest`, `ConsentDecision`, `ILegalHoldStore`, `LegalHold` | `src/Edpf.Abstractions/Compliance/` |

## Verification

**627 automated tests green** — 547 unit, 47 isolation, 18 architecture, 12
walking-skeleton component, 3 conformance contract. Five TFMs build clean.

## Four properties worth stating

**"Not found" and "not authorized" are indistinguishable outward.** The
enumeration defence is now a test: `EdpfNotFoundException` and
`EdpfTenantScopeException` must present identical message and category, and
they do. Only the internal code differs, and it never leaves the process.

**Phase 18 closes the ADR-015 loop.** The redactor surrenders exception
messages by default; the EDPF taxonomy is registered as message-safe *because*
its messages come from the catalogue rather than a throw site. Both halves are
tested — the taxonomy keeps its code in a log, and a third-party exception
that interpolated PHI is still surrendered alongside it.

**De-identification is verified by re-identification attempt**, not a
round-trip. Eighteen identifier categories are enumerable so coverage is
checkable; an unmapped field is **removed by default**, because Safe Harbor
requires the absence of all eighteen and a field nobody classified is a field
nobody checked. Date shifting is per-subject constant, so "the fever started
three days before admission" survives while absolute dates do not.

**Hierarchical scopes replace three parallel features.** The original
specification listed "Department Security", "Hospital Security" and "Facility
Security" separately; they are one model at different depths. Containment is
prefix-based **on whole segments**, so `cardio` does not contain
`cardiology` — a substring check would silently widen every grant whose name
is a prefix of another's.

## The architecture test earning its keep

`Edpf.Security` importing `System.Security.Cryptography` failed the build.
That was correct: the rule exists so crypto lives in one reviewed place, and a
new assembly touching it demanded a decision. `Edpf.Security` is now an
explicit, single-entry exemption with the reasoning recorded in the test —
adding a second should be a deliberate architectural act, which is why the
list is explicit rather than a pattern.

## Tier 3, fourth appearance

`DateOnly`, `System.Index`/`System.Range`, `IsExternalInit`, and the static
`HMACSHA256.HashData` overload are all absent on net472/net48. Each was
resolved with a portable equivalent and a comment naming ADR-002, rather than
by adding polyfill packages to `Edpf.Abstractions` (EDPF0001) or reaching for
`#if` (EDPF0002). One of these turned into a genuine improvement:
`SafeHarborIdentifier.None` was added to satisfy CA1008 and is now the
positive statement "someone classified this field and found it safe" — which
is exactly the evidence an auditor asks for, and distinct from *unmapped*.

## Carried forward to Gate G4

| Requirement | Phase | Why not now |
|---|---|---|
| NIST CAVP-style test vectors for every primitive | 20 | The vectors validate the platform's crypto library, not EDPF's use of it; belongs with the FIPS-mode work |
| Key rotation under sustained load, zero downtime | 20 | Needs a live KMS and load |
| **Independent cryptographic design review** | 20 | Explicitly must be done "by someone who did not write the code" — cannot be self-certified, by definition |
| JWT/SAML attack batteries | 21 | Needs the Phase 21 token pipeline; the skeleton validates JWTs but does not yet own the full provider surface |
| Break-glass end-to-end incl. notification | 21 | Contracts exist ([`IBreakGlassService`](../../../src/Edpf.Abstractions/Tenancy/ITenantProvisioner.cs)); the notification path is Phase 26 |
| Audit completeness by coverage analysis | 19 | Needs the analyser, Phase 33 tooling |
| DSAR across every store incl. blob and search | 22 | Needs those backends (Gate G3 carry-forward) |
| Independent security review | 22 | Same reason as the crypto review |

**Gate G4 is NOT passed**, and two of its criteria — the independent
cryptographic design review and the independent security review — are ones no
amount of further work here can satisfy. They require a reviewer who did not
write the code, which is the point of them.

## Programme note

Four waves in, the pattern is consistent and deliberate: contracts plus the
safety properties that cannot be retrofitted, verified by test; infrastructure-
and reviewer-bound criteria deferred with reasons. Wave 4 converted more to
verified capability than Waves 2 and 3 did — de-identification, scopes, the
error contract and consent are all fully testable — which is what I expected
of this wave and worth noting held true.
