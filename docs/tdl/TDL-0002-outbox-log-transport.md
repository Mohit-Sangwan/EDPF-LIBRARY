# TDL-0002: Skeleton outbox "transport" is a structured log entry

**Phase:** p02 · **Status:** Accepted · **Constraining ADR:** ADR-003

Phase 02 requires "one outbox message published on create — proving ADR-003's
mechanism at the smallest scale". The mechanism under proof is the
transactional enqueue + claim/mark dispatch semantics, not a broker
integration. Dispatch therefore emits a correlated, classification-clean
structured log entry standing in for a publish; `IMessagePublisher` and real
transports are Phase 26.

Consequence accepted: delivery guarantees beyond at-least-once dispatch
accounting are untested until Phase 26 — acceptable because no consumer
exists yet.
