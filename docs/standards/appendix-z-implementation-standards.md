# Appendix Z — Implementation Standards

**The engineering rulebook. Read on day one; return to it for every pull
request.**

**Status: normative.** Where this conflicts with prose elsewhere, this wins.
Changes require a Standards RFC with two squads' agreement (Z.21).

---

## Z.1 Repository & folder structure

```text
edpf/
├── Directory.Build.props              # TFMs, analyzers, nullable, deterministic
├── Directory.Packages.props           # central package management — one version, one place
├── .editorconfig                      # style as build error, not suggestion
├── .github/
│   ├── workflows/                     # CI/CD (Z.13)
│   ├── PULL_REQUEST_TEMPLATE.md       # Z.5.3
│   └── CODEOWNERS                     # squad ownership per path
├── src/
│   ├── Edpf.Abstractions/             # interfaces only — ZERO package refs
│   ├── Edpf.Core/                     # shared kernel
│   ├── Edpf.Compatibility/            # ONLY place #if TFM is permitted
│   └── Edpf.<Pillar>/                 # Data, Security, Audit, Tenancy, Storage…
├── providers/                         # per engine; restricted licences isolated
├── verticals/                         # Edpf.Healthcare.* · Edpf.Finance.*
├── tests/
│   ├── Edpf.UnitTests/
│   ├── Edpf.IntegrationTests/
│   ├── Edpf.ConformanceTests/         # the suite every provider must pass
│   ├── Edpf.IsolationTests/           # adversarial cross-tenant suite
│   ├── Edpf.SecurityTests/            # injection, XXE, ReDoS, zip corpora
│   ├── Edpf.ArchitectureTests/        # layering + diagram conformance
│   └── Edpf.Benchmarks/
├── samples/                           # reference applications (Z.17)
├── tools/                             # CLI, generators, diagram emitter
└── docs/
    ├── adr/           tdl/            # architecture + technical decision logs
    ├── phases/                        # per-phase folders (Z.16)
    ├── compliance/    standards/
    └── api-surface/                   # public API snapshots, diffed in CI
```

**Rules.** One project per package. Test project names mirror the project
under test. No project references across `providers/` — providers depend on
`src/` only. `samples/` is never referenced by `src/`.

## Z.2 Naming standards

| Rule | Why |
|---|---|
| Package = `Edpf.<Pillar>[.<Provider>]` | The package graph must read as an architecture |
| Namespace mirrors folder path exactly | No hunting for types |
| Async methods end `Async` and take a `CancellationToken` | No exceptions, including "internal" methods |
| Error codes `EDPF-<AREA>-<NNNN>`, stable forever | Support and customers depend on them |
| DB: `TenantId` first in every clustered index | Isolation must be free, not costly |

## Z.3 Coding standards — the non-negotiables

1. `Nullable=enable`, `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-all`.
2. `ConfigureAwait(false)` on every await in library code.
3. `CancellationToken` on every async public method — no overload without one.
4. Never `DateTime.Now` or `DateTime.UtcNow`; use `IClock`.
5. Never `System.Random` in any security context; `RandomNumberGenerator` only.
6. Never string-concatenate SQL, a log message, or a file path.
7. `sealed` by default; `virtual` only with a documented extension reason.
8. Return `Result<T>`, not exceptions, for expected failures; exceptions for
   exceptional ones.
9. No `catch (Exception)` without rethrow or a documented reason.
10. No `async void` outside event handlers.
11. No public mutable static state.
12. No `#if` outside `Edpf.Compatibility`.
13. No `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()`.
14. Money is `decimal` + currency code; never `double` or `float`.
15. Every public type and member carries XML documentation.

## Z.4 Analyzer rule set

Configured in [`.editorconfig`](../../.editorconfig); **error** severity blocks
the build. Rules marked *(test)* are currently enforced by
`Edpf.ArchitectureTests` at identical severity until the dedicated Roslyn
analyzer package ships in Phase 33.

| ID | Rule | Severity |
|---|---|---|
| `EDPF0001` | `Edpf.Abstractions` must have zero package references | **Error** *(test)* |
| `EDPF0002` | No `#if` outside `Edpf.Compatibility` | **Error** *(test)* |
| `EDPF0003` | No `DateTime.Now` / `DateTime.UtcNow` | **Error** *(test)* |
| `EDPF0004` | No `System.Random` in security paths | **Error** *(test)* |
| `EDPF0005` | Do not log a type carrying a classified member | **Error** |
| `EDPF0006` | Do not log or serialize `SecretValue` | **Error** |
| `EDPF0007` | Async public method must accept `CancellationToken` | **Error** *(test)* |
| `EDPF0008` | `ExecuteRawUnsafe` usage must carry `[JustifiedRawSql]` | **Error** |
| `EDPF0009` | Repository query on `ITenantScopedEntity` requires tenant context | **Error** |
| `EDPF0010` | No `double`/`float` for a monetary value | **Error** |
| `EDPF0011` | Singleton must not capture a scoped service | **Error** |
| `EDPF0012` | Certificate validation must not be disabled | **Error** |
| `EDPF0013` | Public API change requires a `PublicAPI.Unshipped.txt` entry | **Error** |
| `EDPF0014` | `ConfigureAwait(false)` required in library code | **Error** (CA2007) |
| `EDPF0015` | Prefer `IAsyncEnumerable` over a materialised list for unbounded reads | Warning |
| — | Microsoft.CodeAnalysis.NetAnalyzers (all CA rules) | Error via `TreatWarningsAsErrors` |

**Deliberate exceptions**, each justified in `.editorconfig` and never
suppressed inline: CA1510–CA1513 (throw-helpers absent on Tier 3 TFMs),
CA1819 (crypto buffer carriers — allocation budget), CA1000 (the
strongly-typed id pattern), CA2225 (named alternates exist).

## Z.5 Git standards

**Branching.** Trunk-based. Feature branches ≤ 3 days. `main` always
releasable. `feature/<phase>-<slug>` · `fix/<issue>-<slug>` ·
`release/vX.Y` · `hotfix/<cve|issue>-<slug>`.

**Commits.** Conventional Commits extended with a phase scope:

```text
<type>(<phase>): <subject>

<body — what changed and why, not how>

Refs: FR-009, BR-001
ADR: ADR-030
```

Types: `feat` `fix` `perf` `refactor` `test` `docs` `build` `ci` `chore`
`revert` **`sec`**. Scope is the phase id (`p12`, `p08b`, `p24b`). Subject is
imperative, ≤ 72 chars, no trailing period.

A commit touching a security path uses `sec`, or `feat` with a security
footer. A commit changing public API without a `PublicAPI` entry fails CI.
**No commit message may contain a secret, a customer name, or PHI.**

**Pull requests** use [the template](../../.github/PULL_REQUEST_TEMPLATE.md).

## Z.6 Code review checklist

A **No** on any line blocks approval.

**Correctness** — does it do what the story says? · boundaries and null/empty
handled? · is the failure mode explicit? · is cancellation honoured?
**Security** — all input validated and parameterised? · could this cross a
tenant boundary? · does any log, exception or error carry classified data or
internals? · new secrets through `ISecretStore`? · does an error message
enumerate anything useful to an attacker?
**Data** — is the tenant filter unavoidable? · is classification declared on
new fields? · is concurrency handled? · does the query use an index?
**Design** — does it follow the constraining ADRs? · does it belong in this
layer? · Principle 0: should this be integrated rather than built?
**Tests** — do they fail if the code is wrong? · is there a negative case? ·
an isolation case for any new data path? · is anything asserted only by not
throwing?
**Operability** — what does this log, trace and measure? · what alert fires? ·
what does the runbook say?
**Maintainability** — readable in two years? · naming right? · complexity
justified?

**Reviewer conduct:** review the code, not the author · distinguish
*blocking* from *suggestion* explicitly · a `nit:` prefix means non-blocking ·
approving means you would be comfortable being paged for it.

## Z.7 Unit test standards

Naming `Method_Scenario_ExpectedResult` · AAA with blank-line separation ·
one logical assertion per test · no shared mutable state · no `Thread.Sleep`
· no network, disk or database · deterministic — no `DateTime.Now`, no
unseeded random, no ordering assumptions · `Theory`/`InlineData` for boundary
tables · builders over object-mothers.

**Thresholds:** line ≥ 80 %, branch ≥ 70 %, **mutation ≥ 60 %** on core
assemblies. Coverage without a mutation score is a vanity metric — a suite
can execute every line and assert nothing.

**Must be tested:** every public method's happy path · every documented
failure mode · every boundary (0, 1, max, max+1, null, empty) · every guard
clause · every `Result` failure branch.

## Z.8 Integration test standards

Testcontainers only — never a shared environment · each test provisions and
disposes its own data · runs in any order · asserts real behaviour, not mock
interactions · tiered per ADR-008 (Tier A every commit, B nightly, C weekly).

**Every provider passes the identical conformance suite** — that suite is the
definition of "supported" (Z.12).

**Mandatory categories:** CRUD round-trip fidelity · transaction
commit/rollback/savepoint · concurrency conflict · pagination boundaries ·
streaming with bounded memory · cancellation mid-operation · connection
failure and recovery · **cross-tenant access via every route** · encryption
round-trip and key rotation · audit chain continuity.

## Z.9 Performance benchmark standard

BenchmarkDotNet, `[MemoryDiagnoser]`, release build, server GC, minimum three
warmups, pinned hardware profile in CI. Results recorded to the baseline
(`EDPF-BNC-001`); **a regression over 5 % fails the build.**

**Publish measured numbers, never adjectives.**

## Z.10 Security checklist (per pull request)

- [ ] All input validated, bounded, canonicalised before use
- [ ] All SQL/NoSQL parameterised; no dynamic query construction from strings
- [ ] Output encoded for its sink (HTML, CSV, JSON, log, filename)
- [ ] No classified data in logs, exceptions, traces, metrics or responses
- [ ] Tenant boundary enforced in code, not by caller discipline
- [ ] Field-level authorization applied to any new projection
- [ ] Secrets via `ISecretStore`; none in code, config, tests or fixtures
- [ ] Crypto via `ICryptoProvider`; no direct `System.Security.Cryptography`
- [ ] New external call: TLS enforced, certificate validated, timeout set,
      SSRF-guarded
- [ ] New file path: traversal blocked, tenant-namespaced, extension and
      magic-byte checked
- [ ] New endpoint: authenticated, authorized, rate-limited, audited
- [ ] Dependency added: licence checked, CVEs clean, provenance verified
- [ ] Threat model updated if attack surface changed

## Z.11 Documentation checklist

XML docs on every public type and member · non-obvious decisions explained as
*why*, never *what* · phase LLD updated · ADR written for an architectural
decision, TDL entry for a smaller one · README updated if setup changed · API
reference regenerates cleanly · sample updated if the public surface changed ·
migration note if behaviour changed for existing consumers · runbook added or
updated for a new failure mode · every documentation code sample compiles.

## Z.12 Provider certification checklist

A provider is **Supported** only when every box is ticked; until then it is
Experimental or Preview.

`IDataProvider` + `IProviderCapabilities` implemented with **honestly
declared** capabilities (a claimed capability is tested) · `ISqlDialect` and
`ITypeMapper` complete · **conformance suite 100 %**, no skips · type
round-trip proven by property-based test · transaction semantics verified
incl. isolation levels and savepoints (or capability declared false) · native
error codes mapped to the EDPF taxonomy · injection corpus passed on every
query entry point · cross-tenant isolation suite passed · bulk operations
verified incl. encryption and audit parity · migration runner verified incl.
concurrent-startup safety · streaming verified with bounded memory at 10 M
rows · cancellation honoured mid-operation · connection failure, retry and
circuit-breaker verified · benchmarks published vs. the native driver ·
licence recorded, restrictive drivers isolated · documentation complete ·
DRB approval recorded.

## Z.13 CI/CD standards

Every build reproducible and deterministic · no manual step between commit
and staging · pipeline-as-code, reviewed like source · secrets from the
platform vault, never pipeline variables · artefacts signed, provenance
attested (SLSA L3) · a failing pipeline is fixed before any new work merges —
**a red `main` is a stop-the-line event** · commit-gate budget 15 minutes;
exceeded means parallelise or tier, never lower the bar.

## Z.14 Versioning standards

SemVer. Public API diffed in CI; a breaking change without a major bump fails
the build. Pre-release `-alpha.N` → `-beta.N` → `-rc.N`. Every package in a
release shares the version. API versioning is URI-based (`/api/v1/`);
`Deprecation` and `Sunset` headers per RFC 8594; minimum 12-month deprecation
window for LTS.

## Z.16 Phase implementation folder

Every phase produces `docs/phases/<phase-id>/`:

```text
00-README.md              # purpose, scope, constraining ADRs, status
01-requirements.md        # FR/BR subset this phase satisfies
02-hld.md                 # high-level design + diagrams
03-lld.md                 # low-level design
04-interfaces.md          # contracts added/changed
05-models-dto.md          # entities, DTOs, events introduced
06-configuration.md       # options, keys, defaults, reload semantics
07-security.md            # threat model delta, controls, STRIDE mapping
08-compliance.md          # control → clause mapping added
09-test-plan.md           # unit, integration, isolation, security, perf
10-benchmarks.md          # measured numbers vs. budget
11-usage.md               # examples + sample app reference
12-operations.md          # runbook, alerts, failure modes
13-extension-points.md    # what a third party can extend
14-completion-report.md   # gate input
15-release-notes.md       # consumer-facing changes
```

**Rule:** the folder is created at phase start and filled as work proceeds,
never written retrospectively at the gate. A gate review with an empty folder
is a failed gate regardless of the code. Files with nothing to say for a
given phase are omitted rather than filled with placeholders — the Production
Bar forbids placeholder content.

## Z.17 Reference application matrix

| # | Host | TFM | Demonstrates | Release |
|---|---|---|---|---|
| 1 | ASP.NET Core Web API | net8/net10 | Full stack, grid protocol, FHIR export | v0.9 |
| 2 | ASP.NET Core MVC | net8/net10 | Server-rendered UI, WCAG 2.2 AA | v0.9 |
| 3 | Blazor Server | net8/net10 | Realtime, grid, live vitals | v0.9 |
| 4 | Worker Service | net8/net10 | Jobs, outbox dispatch, file transfer | v0.9 |
| 5 | Console | net8/net10 | CLI usage, bulk import | v0.9 |
| 6 | ASP.NET MVC (legacy) | net48 | Tier 3 surface, brownfield coexistence | v0.9 |
| 7 | ASP.NET WebForms | net48 | Tier 3, legacy scope adapter | v0.9 |
| 8 | Windows Service | net48/net8 | On-prem, DPAPI secrets, serial device | v1.0 |
| 9 | Azure Functions | net8/net10 | Serverless, cold-start budget | v1.0 |
| 10 | Blazor WebAssembly | net8/net10 | Client SDK, offline queue | v1.1 |
| 11 | MAUI | net8/net10 | Mobile security, push, MASVS | v1.1 |

## Z.18 Benchmark publication set

Published with every release, measured not asserted, with hardware profile
and comparison baseline stated: CRUD single-row (vs. raw ADO.NET, Dapper) ·
CRUD encrypted · bulk insert/update/merge · transaction commit latency ·
offset vs. keyset paging at 1M/10M/100M rows · grid protocol composite query ·
dynamic filter compilation · search query and index latency · encrypt/decrypt
per field size · key resolution with and without cache · cache
hit/miss/stampede · file upload/download throughput at 1 MB/100 MB/5 GB ·
SFTP throughput · queue publish/consume throughput · outbox dispatch lag under
load · audit write overhead (% of p99) · cold start per host type · memory
allocation per request.

## Z.19 Security validation set

Per release: SAST (every commit) · DAST against staging · dependency scanning
with a build-failing policy · secrets scanning (commit + history) · container
image scanning · IaC scanning · fuzz corpora on every parser · **adversarial
isolation suite** · injection corpora across all providers and protocols ·
authentication and authorization attack batteries · external penetration test
(annual + pre-major) · external cryptographic design review (pre-1.0, then on
algorithm change).

## Z.20 Interoperability certification set

FHIR R4 (Touchstone, US Core, India NRCeS, Bulk Data `$export`) · HL7 v2
(message corpus incl. malformed, ACK/NAK, MLLP) · DICOM (dcm4che, DICOMweb,
**PS3.15 de-identification verified by re-identification attempt including OCR
of pixel data**) · terminology (SNOMED CT subsumption, ICD-10/11, LOINC,
RxNorm, UCUM property tests) · ABDM sandbox and consent-artefact lifecycle ·
IHE profile alignment (PIX/PDQ/XDS) · IHE Connectathon participation.

## Z.21 Baseline freeze & change control

The specification is closed to capability additions.

| Change type | Route | Approver | Evidence required |
|---|---|---|---|
| Typo, clarification, cross-reference | Direct edit | Chief Architect | — |
| **New ADR** | ADR process | AGC | Options analysis |
| **ADR amendment** | Supersession — never edit in place | AGC + affected board | Evidence the revisit trigger fired |
| **New capability** | Stopping rule | TSC + Sponsor | **A named design partner is blocked without it** |
| **Phase scope change** | Phase change request | TSC | Impact on gates, timeline, dependencies |
| **NFR target change** | NFR amendment | Sponsor + Chief Architect | Measurement showing the target is wrong |
| **Standard change (this document)** | Standards RFC | AGC | Two squads' agreement |

**Superseded ADRs are never deleted.** They record why the current answer is
the current answer — the only thing that stops a decision being relitigated
every six months.

## Z.22 Day-one onboarding path

Target: first merged pull request within three days.

1. **This document** — the rules.
2. [Product vision and NFRs](../phases/p00-discovery-decisions/01-requirements.md) — what and why.
3. [Principle 0 and the golden rules](../../README.md#non-negotiables) — how decisions are made.
4. [ADR-012 request pipeline](../adr/ADR-012-request-pipeline.md) — how a request flows and what happens automatically.
5. [The walking skeleton](../phases/p02-walking-skeleton/11-usage.md) — clone it, run it, break it, fix it.
6. [The interface catalogue](../phases/p01-foundations/04-interfaces.md) — the contracts.
7. Your phase's `docs/phases/` folder — the work.
8. First PR: a test, on a low-criticality module, reviewed against Z.6.
