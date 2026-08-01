# Wave 2 — Test plan

## Phase 08 §⑤ — the injection corpus

**32 payloads × 7 entry points × 2 dialects = 226 assertions, all passing.**

Payloads span classic tautologies, comment and statement terminators, UNION
extraction, blind and time-based probes, stacked statements, `xp_cmdshell`
and `sp_executesql`, URL- and hex-encoded variants, JNDI, MongoDB operator
injection (`$ne`, `$gt`, `$where`), bracket and quote breakouts, and a
payload that spells out a competing `TenantId` predicate.

| Entry point | Test | Property asserted |
|---|---|---|
| Filter value | `FilterValue_HostilePayload_ProducesIdenticalSqlToBenign` | **Byte-identical SQL** to a benign value; payload appears only as a parameter |
| Field name | `FieldName_HostilePayload_IsRejectedNotExecuted` | Rejected at metadata resolution; never reaches SQL |
| Sort field | `SortField_HostilePayload_IsRejectedNotExecuted` | Rejected |
| Projection field | `ProjectionField_HostilePayload_IsRejectedNotExecuted` | Rejected |
| `IN` clause | `InClauseValues_HostilePayloads_AreAllParameterised` | Every element parameterised individually |
| Keyset cursor | `KeysetCursor_HostilePayload_IsParameterised` | Cursor values parameterised |
| `LIKE` operators | `LikeOperators_HostilePayload_NeverReachSql` | All three LIKE forms parameterise |

Two structural assertions back them up: a generated statement contains **no
quote character** at all outside the fixed `ESCAPE '\'` clause (a stray quote
would mean a value had been inlined), and caller-supplied LIKE wildcards are
escaped so a search for `50%` cannot silently become match-everything.

## Phase 10 §⑤ — the adversarial tenant suite

Every cross-tenant route, blocked:

| # | Route | Outcome |
|---|---|---|
| 1 | No tenant context at all | Refused with `EDPF-AUTHZ-2102`, category NotFound — **never** "all tenants" |
| 1b | No tenant context on the keyset path | Refused identically |
| 2 | Empty specification | Tenant predicate still emitted |
| 3 | Filtering `TenantId` = another tenant | Adds a predicate; cannot replace the framework's, so it matches nothing |
| 4 | `OR` with an always-true clause | Caller tree is parenthesised and ANDed *after* the tenant predicate |
| 5 | Projecting a non-projectable field | `EDPF-AUTHZ-2103` |
| 5b | Restricted projection | Tenant predicate still emitted |
| 6 | Sorting on an encrypted field | Rejected |
| 7 | Filtering an encrypted field | Rejected, **without naming alternatives** — not a schema oracle |
| 8 | Forged keyset cursor | Tenant predicate still emitted and bound to the caller's own tenant |
| 9 | Soft-deleted rows | Filtered by default; the escape demands a written audit reason |
| 10 | Per-tenant binding | Each tenant binds its own identifier |

## Phase 06 — dialect conformance (no engine required)

Identifier quoting rejects six hostile forms rather than escaping them ·
schema-qualified names quote each part so a dot cannot smuggle structure ·
over-length identifiers are refused rather than silently truncated (63 on
PostgreSQL) · parameter names must be alphanumeric · pagination syntax
differs correctly per engine and both parameterise · keyset predicates expand
lexicographically, honour descending columns, and reject a mismatched cursor.

**Capability honesty** is asserted directly: both Tier A providers must
support what the framework's core paths need, and must *differ* where the
engines genuinely differ rather than converging on a comfortable lie —
parameter limits (2100 vs 65535), identifier limits (128 vs 63), and
zero-downtime DDL (false vs true).

## Phase 08 — pagination correctness

A stable tiebreaker is appended **unconditionally** (BRL-017) and is not
duplicated when the caller already sorts by id · offset arithmetic is correct
at page boundaries · the first keyset page carries no cursor predicate · a
cursor whose shape does not match the sort is rejected rather than silently
skipping or repeating rows · page size over the maximum is refused.

## Phase 09 §⑤ — saga compensation

Steps run in order and compensate **in reverse** · failure at any of three
positions compensates exactly the steps that ran (parameterised over all
three) · a throwing step is treated as a failing step rather than escaping ·
**compensation failure escalates and stops immediately**, deliberately leaving
earlier steps uncompensated because layering further changes onto an
inconsistent state makes the manual repair harder · a throwing compensation
escalates rather than propagating · the escalation report states exactly which
steps remain applied, which is the information a human needs.

## Architecture invariants

`FilterOperators_AreAClosedEnum_NotStrings` · `MigrationPhases_MatchAdr021` ·
`ConcurrencyStrategy_DefaultsToFail` ·
`SagaStatus_DistinguishesCompensationFailure` — each pins an ADR decision so
changing it requires superseding the decision.

## Not covered by this increment

Everything requiring a live engine or sustained load: the ~300-test
round-trip conformance battery, chaos and failover tests via Toxiproxy, the
10-million-row streaming test, benchmarks against raw ADO.NET and Dapper, the
100M-row zero-downtime migration, and the ten-instance concurrent-startup
test. These are Gate G2 criteria and are listed with owners in
[14-completion-report.md](14-completion-report.md).
