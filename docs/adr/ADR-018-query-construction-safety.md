# ADR-018: Query construction safety

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Chief Architect, Security Architect, Squad A lead

## Context

The query surface is the single most attacked part of any data framework
(OWASP A03, CWE-89/943). Every EDPF consumer will build queries from
user-supplied input — filters on a grid, a search box, a report definition —
and the framework decides whether that is safe by construction or safe by
discipline.

## Options considered

1. **Parameterise by convention**, with review and training. The industry
   default, and the reason SQL injection is still in the OWASP top three
   after two decades. One missed call site is a breach.
2. **Sanitise caller input** — escape quotes, strip keywords. A denylist
   against an attacker with unlimited attempts and a decade of published
   bypasses. Loses.
3. **Make injection unrepresentable.** No API accepts a caller string that
   becomes SQL. Field names resolve against entity metadata; operators come
   from a closed enum; values are always parameters.

## Decision

Option 3, with one deliberate escape hatch.

Three properties hold at every node of the query compiler:

- **Identifiers** come from `IEntityMetadata` and are rendered through
  dialect quoting that **rejects** an illegal identifier rather than escaping
  it. A name needing escape did not come from metadata, so something upstream
  is already wrong and should fail loudly.
- **Operators** come from `FilterOperator`, a closed enum mapped to fixed SQL
  by a `switch`. There is no code path where an operator is a string.
- **Values** are never rendered. Each becomes a framework-named parameter and
  travels in the parameter dictionary.

The consequence is stronger than "injection is prevented": a hostile value
and a benign one produce **byte-identical SQL**. The corpus test asserts
exactly that across 32 payloads, seven entry points and both Tier A
providers — 226 assertions — rather than sampling for known-bad patterns.

**Raw SQL** remains available through a deliberately-named `ExecuteRawUnsafe`
API that requires a `[JustifiedRawSql]` attribute (rule EDPF0008), emits an
audit event, and is reported in CI at every usage site. Removing the escape
entirely would push consumers to bypass EDPF altogether, which is worse.

## Consequences

- Positive: injection defence stops depending on reviewer vigilance. New
  entry points inherit it by construction.
- Positive: error messages name only the field the caller supplied and never
  enumerate alternatives, so a rejected filter is not a schema-discovery
  oracle.
- Negative: callers cannot express arbitrary SQL through the builder — by
  design. Genuinely exotic queries use a custom repository or the audited
  raw path.
- Accepted risk: `ExecuteRawUnsafe` will be used. It is visible, attributed,
  audited and counted, which is the most a framework can do.

## Revisit trigger

A CI report shows `ExecuteRawUnsafe` usage growing rather than shrinking,
indicating the builder is missing an expressive capability consumers
genuinely need.
