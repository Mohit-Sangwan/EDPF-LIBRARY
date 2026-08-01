# ADR-022: Audit event taxonomy

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Compliance Officer, Security Architect, Chief Architect

## Context

Phase 19 must produce an audit trail that satisfies HIPAA §164.312(b) and
SOC 2 CC7.2 **while** satisfying GDPR Art. 17 erasure — the three-way conflict
the original specification listed all sides of without resolving. ADR-006
settled the mechanism (crypto-shredding); this ADR settles what is recorded,
at what granularity, and for how long.

## Options considered

1. **Audit everything, retain forever.** Defensible to an auditor and
   indefensible to a regulator: an immutable record of who accessed whom, kept
   permanently, is itself a permanent personal-data holding.
2. **Audit at the operation level only** ("a read occurred"). Cheap, and
   useless during a breach investigation, which is the moment the trail must
   answer *which records* were touched.
3. **Audit the decision and the subject reference, never the payload**, with
   retention driven by classification and jurisdiction.

## Decision

Option 3.

**What is audited:** authentication; **every authorization decision including
denials** — a denial is the record that proves a control worked; access to
classified fields; modification with before/after images; exports;
break-glass invocation and expiry; configuration change; secret access;
migration execution; and every cross-tenant or administrative operation.

**What a record contains:** tenant, sequence, timestamp, event type,
**pseudonymous subject token**, correlation id, and encrypted before/after
images. It never contains a raw identifier (BRL-006) or plaintext payload.
That single constraint is what lets the chain survive erasure: the record
never held the subject's identifying data to begin with.

**Granularity:** one record per decision, not per row read. A paged read of
fifty patients produces one access record naming the query, not fifty —
otherwise audit volume exceeds data volume and the trail becomes unusable at
exactly the scale where it matters.

**Retention** is policy, not code: per classification and per jurisdiction,
typically 6 years for HIPAA and 7–25 for clinical records depending on
jurisdiction and patient age at treatment. Legal hold outranks both the
schedule and an erasure request; holds are time-bounded and their placement
and release are themselves audited.

**Performance:** audit writes are asynchronous and durably queued, but a lost
audit record **fails the operation**. In a regulated system an unauditable
success is a failure, and the walking skeleton already demonstrates the
in-transaction variant (BRL-005).

## Consequences

- Positive: one design satisfies GDPR Art. 17, HIPAA §164.312(b) and clinical
  retention simultaneously, and every later phase inherits it rather than
  re-deciding.
- Negative: investigators reconstructing "which rows" must join the audit
  record's query reference against the data, rather than reading a row list.
  Accepted: the alternative is an audit store larger than the database.
- Accepted risk: subject tokens are pseudonymous, not anonymous. Anyone
  holding the tokenisation key can re-identify — which is why that key is
  separately destroyable and separately controlled.

## Revisit trigger

A regulator or design partner requires row-level access records; or audit
write overhead breaches its 3% p99 budget at Phase 31.
