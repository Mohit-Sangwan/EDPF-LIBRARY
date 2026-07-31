# ADR-002: Multi-target strategy — tiered TFM surface

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Chief Architect, squad leads

## Context

A shared codebase targeting .NET Framework 4.7.2 **and** .NET 10 cannot be
"async-first, streaming, Argon2id, modern `System.Text.Json`" everywhere —
the requirement as originally written was self-contradictory (Gap #2,
severity C). Brownfield hospital estates still run net472/net48 hosts, so
dropping legacy is not an option either.

## Options considered

1. **Full parity across all TFMs via heavy `#if`.** Preserves the promise on
   paper; in practice produces a lattice of conditional code that rots, and
   the oldest TFM's constraints leak into every file.
2. **Drop net472/net48.** Clean codebase; abandons the brownfield customers
   who are a named target market (F.E: adoption is where this fails).
3. **Tiered surface.** Tier 1 (net8.0, net10.0): full surface. Tier 2
   (net6.0): full minus newest APIs. Tier 3 (net472, net48): reduced surface —
   data access, configuration, logging, security primitives; no
   `IAsyncEnumerable` streaming, no minimal-API hosting, no OTel
   auto-instrumentation. `#if` permitted **only** in `Edpf.Compatibility`.

## Options rejected in part

Spike-B evidence (compiling async + streaming + modern crypto against net472)
is what forced the tiering: the divergence list is real, finite, and
containable behind a polyfill boundary.

## Decision

Option 3. The tier definitions live in `Directory.Build.props`
(`EdpfLibraryTargetFrameworks`); divergence is isolated in
`Edpf.Compatibility` (rule EDPF0002, enforced by architecture test); the
tiered surface is proven by per-TFM builds in CI — all five TFMs compile with
warnings as errors today.

## Consequences

- Positive: modern targets are never dragged down; legacy targets get an
  honest, supportable contract instead of a false promise.
- Negative: Tier 3 consumers see a smaller API; documented per capability.
- Accepted risk: net6.0 is out of Microsoft support; retained deliberately as
  the Tier 2 bridge and reviewed at Phase 32 (`CheckEolTargetFramework`
  disabled with this justification).

## Revisit trigger

Design-partner telemetry shows no Tier 3 consumption for two consecutive
quarters (drop the tier), or a Tier 3 customer needs a capability currently
above the line (promote it explicitly, never via ad-hoc `#if`).
