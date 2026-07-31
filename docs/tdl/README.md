# Technical Decision Log

Smaller-than-ADR decisions (Z.11): scoped to one phase or component,
reversible without board review. Format: one file per decision,
`TDL-NNNN-<slug>.md`.

| TDL | Decision | Phase |
|---|---|---|
| [TDL-0001](TDL-0001-skeleton-ensurecreated.md) | Walking skeleton uses `EnsureCreated` + seeding, not migrations | p02 |
| [TDL-0002](TDL-0002-outbox-log-transport.md) | Skeleton outbox "transport" is a structured log entry | p02 |
| [TDL-0003](TDL-0003-ephemeral-dev-keys.md) | Dev harness generates ephemeral JWT/master keys when unconfigured | p02 |
