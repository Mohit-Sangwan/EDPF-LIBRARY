# ADR-015: Telemetry standard & redaction policy

**Status:** Accepted
**Date:** 2026-08-01
**Deciders:** Chief Architect, Security Architect, Compliance Officer

## Context

In a healthcare framework **a log file is a HIPAA-relevant artifact**. Phase
05 is where that is either handled properly or lost permanently: once PHI
reaches a log sink it is in backups, in the SIEM, in the vendor's index, and
in every downstream copy. Retrofitting redaction after thirty phases of
logging code is not feasible.

## Options considered

1. **Opt-in redaction** — mark what is sensitive. Fails the first time a
   developer adds a field and forgets the attribute, which is a matter of
   when, not whether. The default outcome of forgetting is a breach.
2. **Ban structured logging of domain objects by convention**, enforced in
   review. Conventions decay; reviewers miss things; nothing fails.
3. **Opt-out redaction driven by the Phase 01 classification attributes**,
   plus an analyzer that makes logging a classified type a build error.

## Decision

Option 3. **A value is redacted unless it is explicitly known to be safe.**

- OpenTelemetry is the wire standard; Serilog is the default implementation;
  `ILogger` is the only API application code sees.
- `ISensitiveDataRedactor` walks nested objects, collections, dictionaries and
  exceptions, driven by `DataClassificationAttribute`. Anything at
  Confidential or above is replaced; anything unrecognised is also replaced.
- **Exception messages are surrendered by default.** Domain code routinely
  interpolates the very value the caller was forbidden from logging
  (`throw new(...$"patient {mrn} not found")`), and no amount of sanitising
  free text makes it classification-clean. The exception *type* is kept — it
  is what makes the entry actionable — and the correlation id is how the
  incident is investigated. Types whose messages are contractually code-only
  register themselves as message-safe; the Phase 18 taxonomy does exactly
  that.
- `SecretValue` is never rendered, at any depth, by any route.
- Log injection is prevented by neutralising newline and control characters,
  so a value cannot forge additional log entries.
- Sampling: 100% of errors; configurable head-based sampling for success
  paths; tail-based sampling for slow traces.
- Metric tags carry operation, tenant and outcome only. A metric dimension is
  a log line with worse retention — high-cardinality PHI in a tag is both a
  breach and a cost incident.

The **ten-route adversarial suite** (direct, nested, exception message,
exception data, `ToString`, structured property, scope, collection, anonymous
projection, `SecretValue`) is the acceptance test, re-run every wave.

## Consequences

- Positive: "no PHI in logs" becomes a property of the build. The suite
  already caught one real leak during implementation — the exception-message
  route — which is precisely why it exists.
- Negative: redaction costs reflection on the logging path; bounded by member
  plan caching and measured against the Phase 00 logging-overhead budget.
- Negative: surrendered exception messages make third-party exceptions less
  self-describing in logs. Accepted deliberately: the type plus correlation id
  is enough to investigate, and the alternative leaks.
- Accepted risk: a developer can still pre-stringify a domain object before
  logging it. The analyzer (EDPF0005) is what closes that route; the redactor
  cannot un-ring that bell, and the tests state this boundary rather than
  imply a guarantee that does not exist.

## Revisit trigger

The adversarial suite finds an eleventh route; or logging overhead breaches
its budget under sustained load at Phase 31.
