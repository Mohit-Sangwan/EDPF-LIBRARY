# ADR-016: Capability negotiation

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Chief Architect, Squad A lead

## Context

The thirteen target engines differ irreconcilably. Oracle has no `IDENTITY`
in the SQL Server sense; MongoDB has no joins; Cosmos constrains partition
keys; PostgreSQL has no TVP but has arrays. An abstraction must decide what
to do about that, and the decision shapes every phase downstream.

## Options considered

1. **Lowest common denominator.** Expose only what every engine supports.
   Penalises the Tier A providers most customers actually run, and forfeits
   the native features that make them worth choosing.
2. **Emulate missing features.** Where an engine lacks something, synthesise
   it. Produces subtly-wrong behaviour and unpredictable performance — the
   worst failure mode, because it looks like it works.
3. **Honest capability declaration.** Providers declare what they support;
   callers query capabilities and either degrade explicitly or receive
   `EdpfCapabilityNotSupportedException` (EDPF-DATA-3005).

## Decision

Option 3. `IProviderCapabilities` declares thirteen capabilities and three
limits. **A claimed capability is a tested capability** — the conformance
suite verifies every `true` against real behaviour, and a provider that
over-claims fails certification (Z.12).

The framework never emits silently-wrong SQL to paper over a missing feature.
Where a capability is absent it either degrades along a documented path or
refuses; there is no third option.

The honesty requirement has teeth in the reference implementations already:
SQL Server declares `SupportsZeroDowntimeDdl = false`, because although online
index rebuilds exist, most column and constraint changes still take a
schema-modification lock. Declaring `true` would let a migration take an
unplanned outage. PostgreSQL declares `true` for the same capability because
it genuinely adds nullable columns and builds indexes concurrently.

## Consequences

- Positive: provider-specific branches become explicit and testable rather
  than accidental and scattered — which is the mitigation for "the
  abstraction leaks" (Phase 06 §⑧).
- Negative: callers must handle capability absence; the alternative is being
  surprised in production on a provider they did not test.
- Accepted risk: the capability list grows as engines are added. It is a
  closed interface, so growth is a reviewed, breaking-change decision.

## Revisit trigger

A capability turns out to be non-binary in practice — supported only for
certain types or sizes — which means it needs to become a richer descriptor
than a boolean.
