# Phase 08b — Safe Dynamic Query: field-level authorization

**Status:** Complete for field authorization; query governance deferred
**Gate contribution:** G2 (Data Core) · traceability `BO-06 → BR-009 → FR-QUERY-* → TST-AUTHZ-FIELD`
**ADR produced:** [ADR-031 — Filtering and sorting are reading](../adr/ADR-031-field-authorization.md)

## Why this phase exists in this shape

Most of Phase 08b was already delivered:

| Requirement | Where |
| --- | --- |
| BRL-016 — filter, sort and projection accept only metadata-declared fields | Phase 08, `FilterCompiler` / `QueryCompiler` |
| BRL-017 — every sort carries a deterministic tiebreaker | Phase 08, `BuildStableSort`, applied unconditionally |
| BRL-018 — page size capped server-side regardless of request | Phase 08, `PageRequest.MaxPageSize` |
| Fields resolved against a metadata repository, not reflection | Phase 05b (ADR-025) |
| `TST-INJ-*` — injection unrepresentable | Phase 08, 226-assertion corpus |

What was missing was `TST-AUTHZ-FIELD` — and the gap was one I created.

## The defect

Phase 05b added `IFieldMetadata.RequiredScope`, a field-level declaration
meaning *"you need this permission to read me"*. It was declared, stored,
published on the public API surface — and **read by nothing**. A field marked
as requiring a permission was projected to every caller.

That is worse than never having added it. A reviewer seeing `RequiredScope` on
a field definition reasonably concludes the field is protected and stops
looking. The property was security theatre with an API baseline entry.

Found by grepping for its usages before starting this phase, which returned
three hits: the interface declaration, the constructor assignment, and the
property getter.

## What closing it required deciding

### Filtering and sorting are reading

`WHERE Compensation > 100000` never projects a value. A caller who cannot see
the column can still binary-search every value in the table by observing which
rows come back. `ORDER BY Compensation` plus a walk across page boundaries
reconstructs the ordering — and an ordering over salaries is most of the
salaries.

So all three clauses check the permission. Denying projection while permitting
filter is a disclosure control that discloses.

### The refusal must not be an oracle

"You may not read this field" is helpful, and it confirms the field exists. On
a tenant-overlaid entity (ADR-025) the field list is *itself tenant data* — an
entity carrying a `ClinicalTrialArm` column says what that site is running.

A denied field is therefore refused **identically** to one that does not
exist: same code, same category, same sentence shape, differing only in the
name the caller supplied. `RefusalForAProtectedField_IsIdenticalToRefusalForAMissingOne`
asserts this by substituting both field names and comparing the messages.

This extends Phase 18's existing rule — not-found and not-authorized present
identically — from records to columns.

### Default projections omit; explicit projections refuse

Denying the whole query when any protected column exists would make one field
break every default read for everyone below it. But a caller who *names* a
field has asserted they want that one, and silently dropping it would present
a partial row as a whole one.

If no field is readable at all, the query is refused rather than compiled to
an empty `SELECT` — which is invalid SQL and would read as "no data" rather
than "no access".

### Omitting permissions denies

`QueryCompiler`'s `permissions` parameter defaults to
`FieldPermissionSet.None`, not to an all-granting set. A forgotten argument
must fail in the direction that does not disclose.

### No prefix matching

A grant of `compensation.read` must not satisfy a requirement of
`compensation.readAll`; a grant of `comp` must not satisfy everything starting
with those four letters. The bug survives review because the grant *looks*
narrower than the requirement. Matching is exact and ordinal — ordinal because
under a Turkish culture a case-insensitive comparison makes `I` and `ı` the
same letter, and the same grant would authorize differently in two regions.

## Isolation suite

Phase 12 §⑦ makes extending the adversarial suite a mandatory line item for
any phase adding a data path. A field-authorization refusal is the **same
oracle risk in a new place**, so it extends the existing error-enumeration
route rather than becoming a thirteenth. The suite's fixture entity now
carries a field gated on a permission nobody in the suite holds, so the
refusal path is exercised at every gate, permanently.

Isolation suite: 47 → 48.

## Exit criteria

| Criterion | Status |
| --- | --- |
| Field-level authorization on filter, sort and projection | Met |
| Denial indistinguishable from absence | Met — asserted by message substitution |
| Fails closed when permissions are absent | Met |
| Exact permission matching, no prefix widening | Met |
| Isolation suite extended | Met — route 8, +1 test |
| Query cost estimation and `EXPLAIN` pass-through | **Deferred** |

## Deferred, with reasons

**Query governance** — cost estimation before execution, per-profile timeouts,
`EXPLAIN` pass-through, query statistics. All of these need a live engine to
mean anything: a cost estimate that has never been compared against an actual
plan is a number, not an estimate, and per Z.12 a claimed capability is a
tested capability. `EXPLAIN` pass-through is explicitly routed to Phase 06b in
the specification, which is itself engine-bound.

The result-size cap that *can* be enforced without an engine — page size,
clamped server-side regardless of request — was already delivered in Phase 08
as BRL-018.

## What this does not claim

Enforcement is at query **compilation**. Code that bypasses `QueryCompiler` —
a hand-written statement, a bulk export, a direct provider call — is not
covered. And a denied read produces an error but emits **no audit event**;
whether an attempted read of a protected field is itself auditable belongs
with ADR-022's taxonomy. Both are recorded in ADR-031 rather than left to be
discovered.
