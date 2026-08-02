# ADR-031 — Filtering and sorting are reading; a denied field is indistinguishable from an absent one

- **Status:** Accepted
- **Date:** 2026-08-02
- **Phase:** 08b — Safe dynamic query, field-level authorization (TST-AUTHZ-FIELD)
- **Corrects:** an unenforced security property introduced by [ADR-025](ADR-025-metadata-resolved-fields.md)
- **Related:** [ADR-018](ADR-018-query-construction-safety.md) (query safety), [ADR-021](ADR-021-zero-downtime-migration.md), Phase 18 (errors are not an oracle)

## Context

Phase 05b added `IFieldMetadata.RequiredScope` — a field could declare *"you
need this permission to read me"*. **Nothing read it.** The property was
declared, stored, surfaced on the public API, and consulted by no code path.
A field marked as requiring a permission was projected to every caller.

That is worse than not having the property. A reviewer seeing `RequiredScope`
on a field definition reasonably concludes the field is protected, and stops
looking.

Closing it raises two questions that are not obvious.

**Which operations count as reading?** Projection obviously does. But
`WHERE Compensation > 100000` never projects a value, and a caller who cannot
see the column can still binary-search every value in the table by observing
which rows come back. `ORDER BY Compensation` plus a walk across page
boundaries reconstructs the ordering, and an ordering over salaries is most of
the salaries.

**What should the refusal say?** "You may not read this field" is helpful, and
it confirms the field exists. On a tenant-overlaid entity (ADR-025) the field
list is itself tenant data — an entity carrying `ClinicalTrialArm` says what
that site is running.

## Decision

**1. Filter, sort and projection all check `RequiredScope`.**
Reading is reading regardless of which clause the field appears in. Denying
projection while permitting filter is a disclosure control that discloses.

**2. A denied field is refused identically to a field that does not exist.**
Same error code, same category, same sentence shape — differing only in the
name the caller supplied. The message mentions no permission, scope or
authorization. This follows Phase 18's existing rule that not-found and
not-authorized present identically, extended from records to columns.

**3. A default projection omits unreadable fields; an explicit one is
refused.** When a caller asks for "the row", denying the whole query would
mean one protected column breaks every default read for everyone below it. But
when a caller names a field, they have asserted they want *that* field, and
silently dropping it would present a partial row as a whole one. If **no**
field is readable, the query is refused rather than compiled to an empty
`SELECT`, which is invalid SQL and would read as "no data" rather than "no
access".

**4. Omitting permissions denies everything protected.** The compiler's
`permissions` parameter defaults to `FieldPermissionSet.None`, not to an
all-granting set. A forgotten argument must fail in the direction that does not
disclose.

**5. Permission matching is exact and ordinal — no prefix matching.** Prefix
matching is a classic quiet vulnerability: a grant of `compensation.read`
would satisfy a requirement of `compensation.readAll`, and a grant of `comp`
would satisfy everything starting with those four letters. The bug survives
review because the grant *looks* narrower than the requirement. Hierarchy, if
needed, belongs in `AuthorizationScope`, which compares segment-by-segment and
cannot be fooled by a shared prefix.

### Enforcement

Fifteen tests in `FieldAuthorizationTests`, plus an extension to the
adversarial isolation suite's error-enumeration route — a field-authorization
refusal is the same oracle risk in a new place, so it belongs on the existing
route rather than a thirteenth.

## Consequences

### Accepted costs

- **Legitimate callers get a confusing error.** Someone who can see a field in
  the documentation and is told it "is not a queryable field" will file a
  support ticket. That is the same cost 404-not-403 already imposes on record
  access, accepted for the same reason.
- **Every query path must now carry permissions.** A caller that constructs a
  `QueryCompiler` without them silently loses access to protected fields —
  correct, but it will be diagnosed as a bug before it is understood as a
  policy.
- **No hierarchy in field permissions.** A deployment wanting
  `phi.read` to imply `phi.read.demographics` must grant both, or model it as
  an `AuthorizationScope`. That is friction, and it is preferable to prefix
  matching.

### What this does not claim

- **Enforcement is at query compilation, not at the storage layer.** Code that
  bypasses `QueryCompiler` — a hand-written statement, a bulk export, a direct
  provider call — is not covered. The framework makes the safe path
  convenient; it does not make the unsafe path impossible, and an architecture
  test forbidding raw SQL outside the dialects is what stands between the two.
- **Field permissions are not audited here.** A denied read produces an error;
  it does not emit an audit event. Whether an attempted read of a protected
  field is itself an auditable event is a real question and belongs with
  ADR-022's taxonomy.
- **The default-projection omission is reported nowhere yet.** The compiled
  query does not carry a list of withheld columns, so a caller cannot
  currently distinguish "this field is null" from "you did not get this
  field". That is a genuine gap, recorded here rather than left implicit.

## Revisit triggers

- **A caller needs to know which fields were withheld from a default
  projection.** That requires `CompiledQuery` to carry them, and is the first
  thing to add if partial rows cause confusion in practice.
- **Anyone proposes prefix or wildcard matching on permissions.** It is the
  single change most likely to silently widen access here.
- **An attempted read of a protected field needs auditing.** A high-volume
  denial path emitting audit events is its own capacity decision.
- **A second enforcement point appears** — bulk export, a reporting endpoint,
  a direct provider path. Each is a place this decision can be bypassed, and
  each needs the check or an explicit exemption.

  > **Fired once, 2026-08-02.** Phase 33b added bulk export.
  > [ADR-033](ADR-033-export-is-a-security-boundary.md) applies the check at
  > that point, with one deliberate difference: an export **withholds** an
  > unreadable column and records it, where the query compiler refuses. The
  > reasoning is in ADR-033 §4 — a report definition is run by many people, and
  > failing the whole run for one recipient's missing permission leads to the
  > permission being granted to everyone.
