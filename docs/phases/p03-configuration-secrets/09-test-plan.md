# Phase 03 — Test plan

## Secret-store conformance suite

The identical suite runs against every store (Phase 03 exit criteria). Nine
cases: existing secret returns its value · missing secret fails with
`EDPF-CFG-8001` · **the failure names the key but never any value** · rotation
view reports no overlap when not rotating · null and blank keys throw ·
writable stores round-trip a write · read-only stores refuse loudly rather
than silently discarding · writing over an existing secret opens the overlap
with both values readable · the store name is non-empty and credential-free.

Currently passing: `InMemorySecretStore`, `EnvironmentSecretStore`. Cloud
backends join the same suite when they land
([13-extension-points.md](13-extension-points.md)).

## `SecretValue` leakage suite

Ten assertions covering every route a secret escapes by in practice:
`ToString()` returns `***` · string interpolation goes through `ToString` and
so is safe · JSON serialization exposes nothing · `Reveal()` is the only path
to the value · dispose zeroes the buffer and invalidates the instance ·
equality is constant-time · `GetHashCode` derives from length, not content,
so a hash dump leaks nothing · null construction throws · `Empty` is empty but
not missing.

The redactor suite additionally proves a `SecretValue` is never rendered at
any nesting depth
([adversarial route 10](../p05-observability/09-test-plan.md)).

## Rotation

Dual-secret window: the matching handler is notified with **both** values ·
the audit record carries key, store and timing but has no member that could
carry a value · a failing handler returns failure **and leaves the overlap
open**, so traffic keeps flowing on the outgoing credential · unrelated
handlers are not notified · after the window expires the previous value stops
being accepted, so a compromised credential is not honoured indefinitely.

## Transactional hot reload

Six cases: invalid initial configuration fails at boot with `EDPF-CFG-8001` ·
valid initial configuration succeeds · a valid reload is adopted · **an
invalid reload keeps the last-known-good, entirely — not half-applied** · an
invalid reload does not notify listeners · a subsequent valid reload recovers
and clears the stale-configuration flag · unsubscribed listeners stop
receiving notifications.

## Chained store

Precedence: the first store holding the key wins · a miss falls through to the
next layer · writes skip read-only layers and land in the first writable one ·
an empty chain throws at construction · the name describes the precedence
order, for diagnostics.

## Architecture

`ConfigurationPrecedence_Order_MatchesAdr013` and
`ConfigurationPrecedence_SecretStore_IsHighestPriority` assert the declared
order against the ADR, so reordering configuration precedence requires
superseding the decision.

## Not yet covered

Integration tests against live vaults, and the "rotate a database credential
under sustained load, assert zero failed requests" test, both arrive with the
cloud backends. The rotation *mechanism* is proven at unit level; the
zero-downtime claim under load is not yet measured and is therefore not
claimed.
