# Licence

**Status: template pending legal execution.** The licensing *decision* is made
and binding ([ADR-009](docs/adr/ADR-009-licensing.md)); the executed legal
text is a Phase 34 deliverable and a program-readiness item (Appendix F.B).
Until counsel has settled and signed the final text, treat this repository as
**all rights reserved** and contact the maintainers before any use beyond
evaluation.

## The decided model

**Dual: source-available core + commercial enterprise licence.**

- **Core** (`src/`, Tier A providers, `samples/`, `docs/`): source-available
  under Business Source Licence 1.1-style terms — source is readable and
  modifiable, non-production use is granted, and production use beyond the
  additional-use grant requires the commercial licence. A change date
  converts each release to an open-source licence after the agreed period.
- **Enterprise licence:** production use, support SLAs, and the
  certification and compliance evidence packages enterprises actually buy.

## Third-party components

Restrictive drivers — Oracle, SAP HANA, IBM Db2 — ship as **optional
packages** under `providers/` and are never referenced by the core package
graph, so the core ships licence-clean. Where redistribution is not
permitted, the client library is consumer-supplied.

Redis and Elasticsearch integrations are assessed against their current
upstream licences in the phase that builds them, with the assessment recorded
in the [technical decision log](docs/tdl/README.md).

Every build produces a CycloneDX SBOM, and a licence-policy gate fails the
build if a disallowed licence enters the dependency graph.

## Contributions

External contributions require a signed CLA before merge. The CLA text is
part of the same Phase 34 legal package as the licence text above.

## Open items before v0.9

- [ ] Final licence text settled and executed by counsel
- [ ] Change-date period agreed by the sponsor
- [ ] Additional-use grant scope defined (evaluation, development, non-profit)
- [ ] CLA published and tooling wired into the pull-request flow
- [ ] Commercial licence terms, including BAA and DPA templates (Appendix F.B)
