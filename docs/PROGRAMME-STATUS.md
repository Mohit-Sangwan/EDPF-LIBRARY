# Programme status — all nine waves

**Date:** 2026-08-02 · **Baseline:** Revision 12.0 (frozen)

Phases 00–37, plus the Appendix H/I additions 05b, 08c, 17c, 23d, 24b, 24f
and 26f, have been worked through in order. This document is the single place
a sponsor can see what exists, what does not, and why.

---

## Where the programme stands

| | |
|---|---|
| Waves worked | 9 of 9 (Phases 00–37, plus 05b, 08c, 17c, 23d, 24b, 24f and 26f) |
| ADRs accepted | 30 |
| Automated tests | 1082, all passing |
| Target frameworks | 5, building clean with warnings as errors |
| Gates passed on engineering criteria | **G0, G1** |
| Gates with outstanding criteria | G2–G9 |

Two gates passed outright. That number is low on purpose, and the reason is
the same in every case: the later gates are satisfied by infrastructure,
external parties, or elapsed time, not by more code.

---

## The gates, honestly

| Gate | Wave | Status | What is missing |
|---|---|---|---|
| **G0 Viability** | 0 | ✅ Passed | Sponsor's recorded go/no-go |
| **G1 Foundation** | 1 | ✅ Passed | — |
| G2 Data Core | 2 | Contracts complete | Conformance batteries, chaos failover, streaming at scale — all need live engines |
| G3 Services | 3 | Contracts complete | Redis, search cluster, blob storage, broker |
| G4 Trust | 4 | Contracts complete | **Independent cryptographic and security reviews** |
| G5 Domain | 5 | Contracts complete | FHIR/HL7/DICOM interop against live test servers |
| ↳ *vertical boundary* | 5 | ✅ Enforced | — ADR-024 and three core-neutrality tests |
| G6 Integration | 6 | Contracts complete | Broker-down chaos test |
| G7 Operable | 7 | Logic complete | **DR drill**, full 13-engine matrix, 72-hour soak |
| G8 Adoptable | 8 | Tooling complete | **External team usability test** |
| G9 Release | 9 | Documents complete | **Pen test, red team, design partners, signed packages, support org** |

---

## Four criteria that cannot be self-satisfied

These deserve separating from the merely-not-yet-done, because no amount of
further engineering moves them:

1. **Independent cryptographic design review** (G4) — requires, by
   definition, someone who did not write the code.
2. **Independent security and architecture review** (G4, G9) — same.
3. **Red-team exercise** (G9) — the adversarial isolation suite covers twelve
   routes and passes, but I wrote both the defences and the attacks. It is
   evidence that twelve *known* routes are closed, not that a thirteenth does
   not exist.
4. **External team builds an app in under a day** (G8) — a usability test
   with strangers. Reading my own documentation and finding it clear is
   precisely the bias the criterion exists to defeat.

---

## What was built, by property rather than by phase

The through-line of the programme is that certain classes of mistake were
made **structurally impossible** rather than merely discouraged:

| Property | Mechanism | Evidence |
|---|---|---|
| Injection is unrepresentable | Identifiers from metadata (rejected, not escaped); operators a closed enum; values always parameters | Hostile and benign payloads compile to byte-identical SQL — 226 assertions |
| The tenant predicate is unavoidable | Emitted first and unconditionally; unresolved tenant refused, never read as "all tenants" | 12 adversarial routes, coverage machine-checked |
| Cross-tenant blob paths cannot be constructed | Tenant required and prepended; traversal rejected, not normalised | 11 traversal forms refused |
| Unprefixed cache keys cannot be constructed | The key type refuses; the one global shape demands written justification | Collision tests |
| No PHI reaches a log sink | Redaction opt-out by default; exception messages surrendered unless catalogue-sourced | 10 adversarial routes |
| Errors are not an enumeration oracle | Not-found and not-authorized present identically | Error-contract tests |
| Erasure and audit coexist | Crypto-shredding; audit holds tokens, never identifiers | Chain verifies after shredding |
| A lost update is impossible to produce accidentally | Optimistic by default; silent last-write-wins never the default | Concurrency contract |
| A dose cannot be silently mis-converted | Dimension crossing refused; unknown units refused; case-sensitive; decimal throughout | Unit-safety tests |
| Rollout cannot flap or regress | FNV-1a stable bucketing; widening never withdraws | Monotonicity test |
| A formula cannot launder classified data | Classification propagates through every operator and function, including an `IF`'s condition | 19 operations asserted |
| A user-authored formula cannot reach I/O or reflection | No AST node for member access or invocation; capabilities absent from the assembly | 12 hostile sources refused; assembly-scope architecture test |
| A quality report cannot disclose the data it describes | Classified columns yield shape only; small-cell suppression regardless of classification | Profile tests across all six classification levels |
| A mislabelled specimen cannot come from a missing separator | Variable-length GS1 fields separated; separator inside a value refused, not escaped | Round-trip and refusal tests |
| An uncalibrated instrument's reading cannot be recorded | Entitlement checked before content, so a *normal-looking* value is rejected too; an unrecorded calibration is invalid, not valid-by-default | Device platform tests |
| A true emergency cannot be discarded as an outlier | Separate plausible and expected bands, three dispositions — impossible is rejected, abnormal-but-real is flagged for a human | Artefact/emergency tests |
| An incremental sync cannot silently skip records | Composite (timestamp, id) cursor; mandatory safety lag with no default; offset pagination refused unless the set is declared frozen | 1,000 records at one instant read exactly once |

---

## Defects the tests caught during development

Worth recording, because they are the argument for the approach:

| Defect | Found by | Would have surfaced |
|---|---|---|
| Audit written **after** commit — a create could succeed unaudited | Writing Phase 02's transaction | In production, as an unexplainable gap in the trail |
| Exception messages leaked interpolated PHI to logs | Adversarial redaction route 3, first run | At a breach review |
| `AddEdpfCore` duplicated every registration when called twice | Idempotency test | As duplicate singletons, subtly |
| `Edpf.Security` importing cryptography directly | Architecture test | As crypto spreading across assemblies |
| Stale entry in the public-API baseline after a signature change | RS0017 | As a missed breaking change |
| Chained patient merge (A→B then B→C) passed the guard, because the guard only inspected incoming records and B was a *survivor* | Phase 24b reversibility test | **As a clinical-safety incident** — reversing the first merge would restore A after its data had already propagated to C |
| No metadata repository existed in `src/` at all — the query compiler's whitelist had only test doubles to resolve against | Phase 05b, checking the document's own Appendix I.0 finding against this repository | As custom fields being unqueryable, then routed around the safety model |
| Two redaction policies disagreed on `Internal` — the new metadata-driven one redacted it, the shipped ADR-015 one did not | Phase 05b cross-check test | As a field's protection depending on which subsystem looked at it |
| A GTIN-14 test vector I wrote had the wrong check digit | The Phase 17c implementation, on first run | Nowhere — but only because expectations came from GS1's published examples rather than from the encoder. Had they been derived from the code, a wrong encoder would have passed |
| Six assemblies added after ADR-024 sat outside the core-neutrality test's scope — the rule was enforced against a stale list | Phase 24f, widening the list before adding a seventh | As clinical vocabulary re-entering the core through whichever package nobody had listed. All six turned out neutral, but they were neutral *unguarded* |

---

## Recurring cost: ADR-002

The tiered target-framework decision surfaced **five times** as a concrete
design constraint — `IsExternalInit`, `DateOnly`, `Index`/`Range`, static
`HashData`, `[GeneratedRegex]`. Each was resolved with a portable equivalent
or an explicit tiering decision, never with a polyfill in `Edpf.Abstractions`
(EDPF0001) or a stray `#if` (EDPF0002).

That is the ADR working as intended: the cost is real, recurring, and paid
visibly at design decisions rather than accumulating as conditional
compilation. One instance produced a genuine improvement —
`SafeHarborIdentifier.None` now positively records "someone classified this
field and found it safe", which is the evidence an auditor wants.

**Phase 24f is the first time the decision's *benefit* was collected.**
Until then Tier 3 had been paid for six times and cashed in zero. `Edpf.Devices`
is the capability ADR-002 named as the justification for keeping net472/net48:
locally-attached laboratory instruments live in desktop and Windows Service
hosts, and those hosts are the ones still on .NET Framework in the hospitals
that own the analyzers. It built clean across all five target frameworks on the
first attempt — which is what the six prior encounters with the rule bought.

If those devices ever move to hosts running modern .NET, ADR-002's cost/benefit
changes and both decisions should be reopened together. ADR-029 records that as
an explicit revisit trigger.

---

## What a sponsor should do next

Nothing on this list is engineering:

1. **Record the Gate G0 go/no-go.** It is the last cheap exit point and it is
   still open.
2. **Execute the licence text** (ADR-009). Until counsel signs, the repository
   is all-rights-reserved and no adoption conversation can start.
3. **Recruit the two design partners** named as a Phase 00 deliverable.
   Everything from G2 onward needs real workloads.
4. **Engage the external reviewers** — cryptographic, security, architecture,
   red team. These have long lead times and gate G4 and G9.
5. **Stand up the infrastructure**: the 13-engine matrix, brokers, Redis, a
   search cluster, blob storage, and a production-representative environment.
6. **Decide the support and LTS commitment.** Enterprises will not adopt
   without it in writing, and it is a policy decision, not a technical one.

The engineering that could be done without infrastructure, external parties
or elapsed time has been done. What remains is a different kind of work, and
it belongs to the programme rather than to the codebase.
