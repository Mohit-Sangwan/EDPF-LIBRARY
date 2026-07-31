# TDL-0001: Walking skeleton uses EnsureCreated + seeding, not migrations

**Phase:** p02 · **Status:** Accepted · **Constraining ADR:** ADR-005

The skeleton manages schema with `Database.EnsureCreatedAsync()` plus an
idempotent seeder, in Development only. Real migration machinery
(EF Migrations + versioned script runner + expand–migrate–contract) is
Phase 11's deliverable under ADR-005; building it early would generalize
before the vertical slice proves the stack, violating Phase 02 §③
("no generality of any kind").

Production composition refuses to run this path: `EnsureCreated` executes
only under `IsDevelopment()`. The skeleton is non-shippable by charter
(Phase 02 §⑧), and Wave 2 rewrites rather than extends it.
