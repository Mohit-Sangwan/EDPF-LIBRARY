# Shared responsibility model

**Purpose of this document (Phase 36):** to prevent an adopting organization
from believing that using EDPF made it compliant.

That belief is the single most expensive misunderstanding available to a
healthcare software buyer, and it forms easily — a framework advertises
"HIPAA-ready encryption and audit", a procurement team reads it as "HIPAA",
and nobody discovers the gap until a breach notification is due or an auditor
asks who reviews the access reports. This document exists so that
conversation happens at evaluation time instead.

> **EDPF ships controls that enable compliance. Compliance is a property of
> your system, your processes, and your organization. EDPF cannot confer it,
> and no EDPF documentation, marketing or support statement may suggest
> otherwise** (Golden Rule 5).

---

## The division, in one table

| Layer | Who | Examples |
|---|---|---|
| **Framework controls** | EDPF | Tenant isolation, field-level encryption, tamper-evident audit, crypto-shredding erasure, RFC 9457 error contracts, injection-safe query construction |
| **Application decisions** | You | Which fields are PHI, which purposes are lawful, who may see what, what your retention schedule is |
| **Deployment and operations** | You | Key custody, network boundaries, patching, backups, DR drills, log retention, monitoring |
| **Organizational process** | You | Access reviews, workforce training, BAAs and DPAs, incident response, breach notification, DPO appointment |

Three of the four layers are yours. That ratio is not a limitation of EDPF;
it is what compliance actually consists of.

---

## What EDPF provides

Each control below is implemented **and** covered by an automated test that
fails if it regresses. The evidence column names the test, so an auditor can
be shown the mechanism rather than a claim.

| Control | Evidence |
|---|---|
| Tenant isolation across twelve routes | `Edpf.IsolationTests` — 47 tests, route coverage machine-checked |
| Cross-tenant reads answer 404, never 403 | `AdversarialTenantTests`, walking-skeleton gate demonstration 2 |
| Injection is unrepresentable in generated SQL | `InjectionCorpusTests` — 226 assertions, hostile and benign inputs compile identically |
| Field-level encryption of PHI, per-subject keys | `CryptoShreddingTests`, gate demonstration 5 (ciphertext verified in the raw table) |
| Crypto-shredding erasure with audit chain intact | `AuditChainTests.Verify_AfterSubjectErasure_ChainStillValid`, gate demonstration 6 |
| Tamper-evident audit chain | `AuditChainTests` — tamper, deletion and reordering all detected |
| Audit records carry tokens, never raw identifiers | `Write_Subject_NeverStoresRawIdentifier` |
| No PHI reaches a log sink | `AdversarialRedactionTests` — ten routes blocked |
| Errors are not an enumeration oracle | `ErrorContractSecurityTests` |
| De-identification, all 18 Safe Harbor identifiers | `DeidentificationTests` |
| Consent cannot be granted without a lawful basis | `ConsentAndHoldTests` |
| Legal hold blocks erasure | `ConsentAndHoldTests` |
| Unit-conversion safety (mg/mcg class of error) | `UnitConversionSafetyTests` |
| Classification drift detection | `DataClassifierTests`, `edpf classify-schema` |

**Controls that exist as contracts but are not yet proven against live
infrastructure** are listed in the per-wave completion reports under
"carried forward". They are not claimed here. A control matrix that lists
intentions alongside evidence is worse than one that lists only evidence,
because it teaches the reader to stop checking.

---

## What EDPF does not and cannot provide

### It cannot decide what your data is

EDPF encrypts, redacts, audits and export-controls anything you classify as
PHI. **You** classify it. A column nobody marked is a column that receives
none of those protections — which is why `edpf classify-schema` exists, and
why it exits non-zero on a confirmed finding.

### It cannot decide who should see what

EDPF enforces the authorization model you configure, across a hierarchy of
organization → facility → department → unit → resource. Whether a ward clerk
should see a psychiatric note is a clinical governance decision, and it is
yours.

### It cannot operate itself

Key custody, backup verification, DR drills, patching, certificate rotation,
log retention, alert response. EDPF provides the mechanisms and the runbook
hooks; it does not hold your keys or answer your pages.

### It cannot make you a covered entity in good standing

BAAs with your subprocessors. DPAs with your controllers. A named DPO where
required. Workforce training records. Access reviews performed and
documented. Incident response exercised. Breach notification within 72 hours.
None of this is software.

### It is not a medical device

EDPF provides data infrastructure and **does not make clinical decisions**.
If you build clinical decision support on it, you are building a regulated
medical device (FDA SaMD, EU MDR) and you carry those obligations —
including the ones EDPF's presence in your stack does nothing to reduce
([ADR-023](../adr/ADR-023-integrate-do-not-build.md)).

---

## Per-regulation split

### HIPAA Security Rule

| Safeguard | EDPF | You |
|---|---|---|
| §164.312(a)(1) Access control | Tenant isolation, RBAC/ABAC, break-glass mechanism | Role definitions, access reviews, workforce sanctions |
| §164.312(a)(2)(iv) Encryption at rest | Field-level encryption, per-subject DEKs, crypto-agility | Key custody, HSM/KMS operation, rotation schedule |
| §164.312(b) Audit controls | Tamper-evident chain, verification, coverage | **Reviewing the audit records.** A trail nobody reads is not an audit control |
| §164.312(e) Transmission security | TLS enforcement, certificate validation | Network architecture, certificate lifecycle |
| §164.308 Administrative safeguards | — | **Entirely yours.** Risk analysis, sanctions, training, contingency planning |
| §164.314 BAAs | — | Yours, with every subprocessor |

### GDPR

| Article | EDPF | You |
|---|---|---|
| Art. 5(1)(f) Integrity & confidentiality | Encryption, isolation, audit | Operating them correctly |
| Art. 6 Lawful basis | Basis is recorded and enforced; processing without one fails | **Determining** the basis |
| Art. 17 Erasure | Crypto-shredding, provably irreversible | Deciding when erasure applies; handling the request within one month |
| Art. 25 Data protection by design | Classification-driven controls, privacy defaults | Your own design decisions |
| Art. 30 Records of processing | Lineage infrastructure | Maintaining the record |
| Art. 32 Security of processing | The control set above | Risk assessment, DPIA, appropriateness judgement |
| Art. 33/34 Breach notification | Audit evidence to reconstruct scope | **Notifying, within 72 hours** |
| Art. 35 DPIA | Evidence inputs | Conducting it |

### SOC 2

EDPF contributes evidence for CC6 (logical access), CC7 (monitoring) and
PI1 (processing integrity). It contributes **nothing** to CC1 (control
environment), CC2 (communication), CC3 (risk assessment), CC4 (monitoring of
controls) or CC5 (control activities) — those are organizational, and an
auditor will not accept a framework as evidence for them.

---

## Questions worth asking any framework vendor, including us

If a vendor cannot answer these plainly, the gap is the answer:

1. Which of your claimed controls have automated tests, and may I see them?
2. Which claimed capabilities have never been tested against live
   infrastructure?
3. What does your framework *not* do that my compliance programme still
   needs?
4. Has an independent party reviewed your cryptographic design? May I see the
   report?
5. What is your vulnerability disclosure SLA, and your LTS commitment?

EDPF's own answers, honestly: questions 1 and 3 are answered above and in the
completion reports. Question 2 is answered in each wave's "carried forward"
section. **Questions 4 and 5 are not yet satisfied** — the independent
cryptographic review is an outstanding Gate G4 criterion, and the LTS
commitment is an outstanding Phase 37 deliverable. Neither should be assumed
in EDPF's favour until published.
