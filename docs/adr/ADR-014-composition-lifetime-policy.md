# ADR-014: Composition & lifetime policy

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Chief Architect, Security Architect, Squad C lead

## Context

EDPF must wire correctly in DI-native hosts (ASP.NET Core, Worker Service) and
DI-hostile ones (WebForms, WinForms, WPF). In a multi-tenant framework,
lifetime is not an ergonomics concern: the scoped `ITenantContext` is the
isolation boundary, and a component that outlives its scope carries one
tenant's context into the next request.

## Options considered

1. **One monolithic `AddEdpf()`.** Simplest call site; drags every provider's
   dependency graph into every consumer and defeats ADR-009's licence
   isolation.
2. **Write an EDPF container.** Full control over lifetime rules. Principle 0
   failure — `Microsoft.Extensions.DependencyInjection` is maintained,
   understood, and universally supported; EDPF would not be better at it in
   three years.
3. **Feature-module registration over the standard container**, with an
   explicit lifetime policy and mechanical enforcement.

## Decision

Option 3.

**Registration** is per feature module —
`AddEdpfCore().AddSqlServer().AddRedisCache().AddAudit()` — through the public
`IEdpfBuilder`. Third-party providers use the same surface the built-in ones
use; there is no privileged internal path. The builder is itself registered,
so repeated or defensive calls are idempotent rather than silently duplicating
registrations.

**Lifetime policy**, stated once rather than rediscovered per module:
provider factories and stateless helpers are singletons; connections and units
of work are scoped; ambient-context accessors are singletons because their
state rides the async execution context, not the instance.

**Captive-dependency detection is mandatory.** `CaptiveDependencyDetector`
statically sweeps the whole service collection for singletons whose
constructors take scoped services — including through `IEnumerable<T>` — and
fails the build. The container's own `ValidateScopes` catches the same fault
only when the offending service is actually constructed, which is necessary
but not sufficient. `ValidateOnBuild` and `ValidateScopes` are enabled in
**all** environments, not just Development: a misconfigured graph should fail
at boot in production too.

**Legacy hosts** get `EdpfScopeAccessor` for explicit per-operation scopes,
and a service-locator escape hatch that requires a written justification
argument and logs a warning naming the caller — deliberately awkward, so it
does not become the path of least resistance.

## Consequences

- Positive: the most common multi-tenant leak is now a build failure with a
  message that names both offending types.
- Negative: factory and instance registrations cannot be swept statically
  (the factory body is opaque); those remain covered by `ValidateScopes`.
- Accepted risk: net472's DI behaviour could diverge; the identical DI
  conformance suite runs on every TFM.

## Revisit trigger

A captive dependency reaches production despite both mechanisms — indicating
the static sweep needs to cover factory registrations, probably via a Roslyn
analyzer in Phase 33.
