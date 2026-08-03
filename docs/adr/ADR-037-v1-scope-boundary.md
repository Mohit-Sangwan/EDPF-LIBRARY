# ADR-037 — The v1.0 scope boundary: what ships, what waits, what never comes

- **Status:** Proposed — this one needs the sponsor's signature, not an architect's
- **Date:** 2026-08-03
- **Supersedes:** nothing. **Constrains:** the master development prompt's pillar catalogue
- **Related:** [ADR-009](ADR-009-licensing.md) (commercial model), [ADR-024](ADR-024-vertical-package-boundary.md), [ADR-001](ADR-001-data-core-strategy.md)

## Context

The master development prompt names roughly twenty-four platform pillars,
thirteen databases, thirteen host types, eleven AI vendors, sixteen storage
backends and nine communication channels — each to be delivered with BRD, FRS,
SRS, HLD, LLD, ADRs, five diagram families, unit/integration/performance/load/
stress/soak/chaos/security tests, benchmarks, sample applications and
operations documentation.

**What exists is about a fifth of that**, measured against the repository
rather than the specification:

| | Named | Built |
| --- | ---: | ---: |
| Database providers | 13 | **3** (SQL Server, PostgreSQL, SQLite) |
| Host types | 13 | **1** (ASP.NET Core Web API) |
| Platform pillars | ~24 | **~9** |
| AI vendors | 11 | **0** |
| Storage backends | 16 | **0** |
| Communication channels | 9 | **0** |

That is not a criticism of what was built. The nine pillars that exist are
finished to a standard the other fifteen are nowhere near: 1,248 tests, five
target frameworks, provider parity verified on both Tier A engines, and a set
of safety properties that are structural rather than disciplinary.

It is a statement about arithmetic. The programme's own sizing puts the full
catalogue at **45–52 person-years**. Nothing in this repository changes that
number, and no amount of further specification reduces it.

### The finding that should drive this decision

Over one working session, six controls were examined that had been *declared
and never executed*. **Five were broken:**

| Control | Verdict |
| --- | --- |
| `IFieldMetadata.RequiredScope` — field-level authorization | Read by nothing |
| Z.9 benchmark regression gate | No baseline; could not fire |
| Tier A provider-parity suite | Excluded by a filter; hid two real defects |
| Tier 3 (net472/net48) runtime | *Fine* |
| The CI pipeline | Pointed at a solution file that does not exist |
| `edpf check-licenses` | Failed on every natural input |

Every one had passed review. Every one was found late, by accident, in work
already reported complete.

**Breadth is what produced that.** Each additional pillar is another surface
that can be declared, reviewed, believed and never run. Twenty-four pillars at
this quality bar is not twenty-four times the work — it is twenty-four times
the work plus a verification burden that grows faster than the code.

## Decision

**v1.0 ships the core that exists plus four additions. Everything else is
explicitly deferred or explicitly abandoned — in writing, now.**

### Ships in v1.0

Everything currently in `src/`, unchanged in scope:

Core · Abstractions · Compatibility · DI · Data (query, tenancy, consistency,
migrations) · Security · Compliance · Audit · Metadata · Formula · Rules ·
Data quality · Migration kit · Reporting/export · Barcode · Devices ·
Connectors · Licensing · Diagnostics · Globalization · Operations

Plus four additions, chosen because each is ordinary integration work with no
procurement lead time and no research risk:

1. **MySQL and SQLite providers.** Not for coverage — to prove the provider
   model generalises beyond the pair that already produced a cross-provider
   defect. Two engines is a coincidence; four is evidence.
   **SQLite has landed** (17 tests, `SqliteDialect`) and the abstraction held:
   a third dialect required no change to `SqlDialectBase` or `QueryCompiler`,
   and it declares four capabilities *false* — including row-level security,
   which is the case that proves EDPF never leaned on the database to enforce
   tenancy. MySQL remains.
2. **Storage platform** — local, SFTP, S3-compatible, Azure Blob. Every buyer
   needs files.
3. **Communication platform** — email and SMS, provider-abstracted. Every buyer
   needs notifications.
4. **One further host sample** — a Worker Service or Blazor Server slice, to
   demonstrate the framework outside a Web API.

### Deferred to v1.1+, with a named trigger

| Capability | Trigger that unblocks it |
| --- | --- |
| Oracle, Db2, SAP HANA providers | A paying customer names one. Test licences carry 2–4 month procurement lead time and real cost — order on signature, not on hope |
| MongoDB, Cosmos, cloud-managed variants | A design partner's actual topology |
| AI platform, vector search, RAG, MCP/agents | Selling into a buyer who asks. **EU AI Act positioning is the deliverable, not embeddings** — clinical AI is high-risk under a regulation already in force |
| Real-time push (SignalR/SSE) | A dashboard or vitals-monitoring requirement from a real deployment |
| Time-series store | IoMT feeds at a volume that breaks relational storage — measured, not assumed |
| Document generation, e-signature, print | A workflow that needs them, with the regulated e-prescription question answered first |
| Workflow/BPM engine | Beyond what the existing rules platform covers |
| Search platform, background processing | Load that justifies them |
| gRPC, OData, SOAP | An integration partner who requires one |
| Load, stress, soak, chaos testing | Infrastructure existing to run it against |

### Permanently out of scope for this product

Stated so nobody re-litigates them quarterly:

- **Offline-first sync with conflict resolution.** Genuinely hard, and a
  product in its own right. A MAUI client in a connectivity dead zone is a
  real problem and not one EDPF will solve.
- **GraphQL.** An unbounded GraphQL endpoint over a multi-tenant clinical
  dataset is an exfiltration surface, and bounding it properly costs more than
  the API is worth here.
- **Clinical decision support.** Already the ADR-023 boundary: EDPF provides
  data infrastructure and does not make clinical decisions. Crossing it makes
  the product a regulated medical device.
- **Building what can be integrated.** FHIR, HL7 v2, DICOM (ADR-023) — and the
  same reasoning now extends to any capability with a maintained library.

## Consequences

### Accepted costs

- **EDPF will not match the catalogue.** Marketing must describe what ships.
  The document's own compliance rule applies to capability claims too: ship
  controls, not assertions.
- **Some buyers will disqualify it** for a missing pillar. That is a real lost
  sale and preferable to twenty-four half-built ones.
- **"Deferred" decays into "abandoned" without discipline.** The triggers above
  are the mitigation; a deferred item with no trigger is an abandoned item with
  better manners.

### What this does not decide

**The business case is still missing, and it is upstream of this ADR.** Whether
EDPF is a commercial product, an internal platform for your own HIS, or open
source changes the licensing, the support obligation, the documentation burden
and roughly a third of the phases. This ADR assumes *commercial product*
because that is what the licence-policy gate, the entitlement platform and the
shared-responsibility model already imply — **but nobody has written it down,
and if that assumption is wrong this scope is wrong.**

Also untouched, and none of it engineering: budget and person-years, the
support and CVE organisation, BAA/DPA templates, breach-notification runbook,
liability and insurance, and the interoperability certification calendar
(IHE Connectathon dates, ONC, NABH — all with lead times in months).

## Revisit triggers

- **The business-case decision lands and contradicts the commercial-product
  assumption.** Then this scope is rebuilt, not amended.
- **A design partner signs.** Their topology reorders the deferred table
  immediately, and that is the table working correctly.
- **Anything in the permanent-exclusion list is proposed again.** It requires a
  new ADR and a reason that did not exist today — not a meeting.
- **v1.0 ships.** Then this ADR is history and v1.1 gets its own.
