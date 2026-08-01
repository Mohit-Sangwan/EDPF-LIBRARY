# Phase 05b — Metadata Platform

**Status:** Complete
**Gate contribution:** G1 (Foundation) / G2 (Data Core)
**ADR produced:** [ADR-025 — Field definitions resolve through metadata](../adr/ADR-025-metadata-resolved-fields.md)

## Why this phase exists

Not a feature request — a **correction to the plan's own ordering**, which the
master document's fourth review round called *"the most important finding in
this review"* (Appendix I.0).

The dynamic-query safety model resolves caller-supplied fields against entity
metadata. No metadata repository existed anywhere in the plan, so the query
layer was implicitly assuming reflection over compile-time types. Reflection
cannot describe a field a customer created this morning — so a whitelist built
on reflection cannot authorize one, and custom fields would be pushed outside
the safety model rather than inside it.

**Confirmed in this repository before building anything.** `IEntityMetadata`
existed as a contract and `QueryCompiler` consumed it, but the only
implementations were test doubles in `Edpf.UnitTests` and
`Edpf.IsolationTests`. Nothing in `src/` could supply metadata to the query
compiler at all. The defect was real here, not merely on paper.

## Delivered

### `src/Edpf.Metadata`

| File | Contents |
| --- | --- |
| [`MetadataRepository.cs`](../../src/Edpf.Metadata/MetadataRepository.cs) | Compiled entities + tenant overlays, effective-dated, composed into one `IEntityMetadata` |
| [`EntityMetadata.cs`](../../src/Edpf.Metadata/EntityMetadata.cs) | `ResolveField` — the single door every caller-supplied field name passes through |
| [`FieldMetadata.cs`](../../src/Edpf.Metadata/FieldMetadata.cs) | One field definition, compiled or runtime-defined, indistinguishable to consumers |
| [`ProtectionPolicy.cs`](../../src/Edpf.Metadata/ProtectionPolicy.cs) | The one table mapping classification → encryption, redaction, audit, export, erasability, tokenization |
| [`MetadataProtectionResolver.cs`](../../src/Edpf.Metadata/MetadataProtectionResolver.cs) | What every subsystem asks instead of deciding for itself |
| [`DynamicEntity.cs`](../../src/Edpf.Metadata/DynamicEntity.cs) | Runtime-shaped instance; undeclared names cannot be written |
| [`CompiledEntityScanner.cs`](../../src/Edpf.Metadata/CompiledEntityScanner.cs) | Reflection *producing* metadata at startup — never resolving at request time |

### The verification the phase is named for

> *"A runtime-defined PHI-classified field automatically receives encryption,
> redaction, audit, and DSAR inclusion **with no code written** — this single
> test proves the classification-driven architecture actually holds for custom
> fields."*

`RuntimeDefinedClassifiedField_ReceivesEveryProtection_WithNoCodeWritten`
defines a field the way a customer would — at runtime, after the binary
shipped, with nothing but a classification — and asserts all five protections
arrive. Not one line of the test configures any of them.

Three companions make it mean something rather than restate itself:

- `RuntimeDefinedField_AppearsInEverySubsystemsFieldList` — the same question
  asked the way encryption, export and audit each ask it, because a gap
  between any two would be a disclosure no single subsystem's tests would find.
- `RuntimeDefinedField_IsRedactedFromDiagnostics_LikeAnyOtherClassifiedField`
- `RuntimeDefinedField_IsQueryable_WhichReflectionCouldNotHaveAuthorized` —
  the ordering defect made concrete: the query compiler authorizes a filter on
  a field that did not exist when it was compiled, resolves it to its physical
  column, and passes the value as a parameter.

### Cross-tenant metadata isolation

Metadata is tenant data. A field named `ClinicalTrialArm` tells a competitor
what that hospital is running with no value attached — so an overlay is visible
only to its owner, and another tenant's field resolves as **unknown**, not
**forbidden**, because "forbidden" confirms existence.

### Reproducibility

Definitions are effective-dated rather than overwritten, so a form rendered in
2024 reproduces exactly in an audit five years later.
`SameFieldRedefinedAfterTheFirstClosed_IsAllowed` pins the other half:
succession must be possible, or a field could never be corrected — only
abandoned.

## Two problems this phase surfaced

**1. Two redaction policies disagreed.** `ProtectionPolicy` initially redacted
`Internal`; the shipped ADR-015 redactor redacts at `Confidential` and above.
A test caught it. The shipped threshold is the better one — the compiled
scanner defaults untagged properties to `Internal`, so redacting `Internal`
would redact almost everything, and a log where every field reads `[REDACTED]`
is a log nobody can operate from. Engineers respond to that by logging around
the redactor, which is worse than the exposure it was meant to prevent.

Fixed in the policy, and `RedactionThreshold_MatchesTheAdr015Redactor_LevelForLevel`
now pins the two together level by level, so the divergence cannot return
quietly.

**2. The isolation suite was testing hand-rolled doubles.** Both metadata
fixtures were bespoke implementations of `IEntityMetadata`. An isolation suite
proving a property against a double proves it about the double. Both now build
on the production `EntityMetadata` and `FieldMetadata`, so the twelve routes
exercise the pairing that actually ships. Extending `IFieldMetadata` is what
forced the question — the four new members broke both doubles, and replacing
them was the better answer than reimplementing them.

## Exit criteria

| Criterion | Status |
| --- | --- |
| Runtime-defined PHI field receives every protection with no code written | Met |
| Cross-tenant metadata isolation | Met — invisible, and refused as unknown rather than forbidden |
| Metadata version reproducibility | Met — effective dating, with succession |
| Dynamic query resolves runtime-defined fields | Met — the ordering defect is closed |

## Scope boundary

Phase 05b also names `IFormDefinition` and `ILookupProvider` — dynamic forms
with conditional visibility, layout, and lookup hierarchies. Those are
presentation concerns built **on** this model rather than part of it, and
nothing in the ordering defect depends on them.

The physical storage strategies are declared (`TypedColumn`, `SparseColumn`,
`JsonColumn`, `EntityAttributeValue`) and carried through metadata, but the
storage adapters that would act on them are contract-complete rather than
infrastructure-verified. Per Z.12, the claim here is the one that is tested:
**classification, not code, decides protection — identically for both kinds of
field.**
