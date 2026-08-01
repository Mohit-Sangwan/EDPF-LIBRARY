# ADR-025 — Field definitions resolve through metadata, never through reflection

- **Status:** Accepted
- **Date:** 2026-08-02
- **Phase:** 05b — Metadata Platform
- **Corrects:** an ordering defect in the plan itself (Appendix I.0)
- **Related:** [ADR-018](ADR-018-query-construction-safety.md) (query safety), [ADR-015](ADR-015-telemetry-redaction-policy.md) (redaction), [ADR-006](ADR-006-erasure-vs-audit.md) (crypto-shredding)

## Context

The master document's fourth review round found what it calls *"the most
important finding in this review"* — and it is a defect in the plan's
**ordering**, not a missing feature.

The dynamic-query safety model resolves caller-supplied filter, sort and
projection fields against entity metadata. But no metadata repository existed
anywhere in the plan. The query layer was implicitly assuming reflection over
compile-time types.

That assumption breaks the moment a customer adds a custom field, which every
customer does in week one:

- **Reflection cannot describe a runtime-defined field.** There is no
  `PropertyInfo` for a column a hospital created this morning.
- **So a whitelist built on reflection cannot authorize one.** The field is
  unfilterable, unsortable, unprojectable — not by policy, but by accident of
  implementation.
- **So custom fields would be forced outside the safety model**, reached
  through some second path that skips the whitelist. That is precisely where
  they must not be: a custom field is the field *least* likely to have been
  security-reviewed.

Verified against this repository before building: `IEntityMetadata` existed as
a contract and the query compiler consumed it, but **the only implementations
were test doubles.** Nothing in `src/` could supply metadata to the compiler.
The defect was real here, not merely on paper.

## Decision

**Entity and field definitions resolve through `IMetadataRepository`.
Reflection may *produce* metadata for compiled types at startup; it may never
*resolve* a field at request time.**

Four consequences follow, and each is enforced:

1. **Compile-time and runtime-defined fields are the same type, in the same
   dictionary, behind the same `ResolveField`.** A consumer cannot tell which
   produced a given field, and none tries. `IsRuntimeDefined` exists for
   diagnostics, migration tooling and storage planning — never for a
   protection decision.

2. **Protection follows classification, in one table.** `ProtectionPolicy`
   maps a classification level to encryption, redaction, audit, subject-access
   inclusion, erasability and tokenization. Encryption, redaction, audit and
   export all consult it; none decides for itself.

3. **Metadata is tenant-scoped and effective-dated.** A field name alone can
   disclose a business fact, so one tenant's overlay is invisible to another
   — and resolved as *unknown*, not *forbidden*, because "forbidden" confirms
   existence. Definitions are effective-dated rather than overwritten, so a
   form rendered in 2024 reproduces exactly in an audit five years later.

4. **Overlays are additive only.** A custom field cannot shadow a built-in
   one, because shadowing is how a tenant would redefine a PHI field as
   `Public` and strip its protections.

### Enforcement

| Test | Prevents |
| --- | --- |
| `NoProtectionDecision_BranchesOnWhetherAFieldWasRuntimeDefined` | A second, weaker code path for unreviewed fields |
| `ProtectionRequirements_AreDerivedInExactlyOnePlace` | A subsystem forming its own opinion from a classification level |
| `RedactionThreshold_MatchesTheAdr015Redactor_LevelForLevel` | The metadata-driven and reflection-driven redactors drifting apart |
| `RuntimeDefinedField_IsQueryable_WhichReflectionCouldNotHaveAuthorized` | Regression to the reflection assumption |

## Consequences

### Accepted costs

- **A metadata lookup sits on the query path.** Resolution is an in-memory
  dictionary hit, but it is not free, and it is now unavoidable. That is the
  price of custom fields being inside the safety model rather than beside it.
- **Adding a field to a compiled entity means the scanner sees it
  immediately.** A developer adding a property with no
  `[DataClassification]` gets `Internal`, not `Public` — deliberately, since
  forgetting to classify must not be the mistake that publishes data. It does
  mean an unclassified property silently becomes queryable.
- **Effective dating makes metadata append-only in practice.** Correcting a
  definition means closing one and opening another, which is more ceremony
  than editing a row and is the reason the audit reproduces.

### What this does not claim

The verification demonstrates that a runtime-defined classified field is
**selected for** encryption, redaction, audit and subject-access export by the
same resolver every subsystem consults. It does not demonstrate an end-to-end
encrypted write to a live engine — the storage adapters that would consume
`FieldsRequiring(EncryptAtRest)` are contract-complete but not
infrastructure-verified (Z.12). The claim here is precisely the one the
architecture rests on: **classification, not code, decides protection, and it
decides identically for both kinds of field.**

## Revisit triggers

- **A subsystem needs to branch on `IsRuntimeDefined`.** If a legitimate case
  appears, the uniformity claim is narrower than stated and this ADR must say
  where the boundary actually is.
- **`ProtectionPolicy` acquires a second caller-supplied policy instance in
  production.** Configurable protection is configurable weakening; if it is
  genuinely required, the reason belongs in an ADR.
- **A storage strategy other than the four enumerated is needed.** The set was
  chosen to cover the observed cases; a fifth means the model was drawn wrong.
- **Metadata resolution appears in a benchmark regression.** If the lookup
  becomes material, caching enters the design and cache invalidation across
  effective-dated, tenant-scoped metadata is its own ADR.
