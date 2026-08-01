# Phase 03 — Extension points, and what was deliberately deferred

## Extension points

`ISecretStore` is the single seam. A new backend implements three methods,
passes the conformance suite, and is registered into the chain — no consumer
changes, and no interface change is needed to accommodate a future HSM or
CloudHSM (Phase 03 §⑧).

`ISecretRotationHandler` is the seam for anything holding derived credential
state: connection factories (Phase 07), HTTP clients, signing credentials
(Phase 21). Handlers refresh without a restart; in-flight work completes on
the outgoing value.

`IConfigurationValidator<T>` lets any module contribute startup validation to
its own options without the composition root knowing about it.

## Deferred: cloud secret backends

The playbook lists Azure Key Vault, AWS Secrets Manager and HashiCorp Vault
as Phase 03 implementations. **They are not in this increment**, and the
reason is a bar this project has set for itself rather than an oversight.

Z.12 states that a claimed capability is a tested capability, and the
production bar forbids shipping code that cannot be verified. A Key Vault
adapter cannot be integration-tested without a tenant, a managed identity, and
a vault; writing three unverifiable SDK wrappers would produce exactly the
kind of "supported" that means nothing — the failure mode ADR-008 exists to
prevent.

What *was* built is the part that makes those adapters cheap and safe when
credentials are available:

- the contract they must implement (`ISecretStore`);
- the conformance suite that defines what "supported" means for a store, with
  two implementations already passing it;
- the chain that layers them in precedence order, so adding a vault is a
  registration change rather than a code change in any consumer;
- the rotation coordinator, which is backend-agnostic.

Each adapter ships as its own optional package under `providers/`
(`Edpf.Secrets.AzureKeyVault`, `Edpf.Secrets.AwsSecretsManager`,
`Edpf.Secrets.HashiCorpVault`) per ADR-009, so the core graph never carries a
cloud SDK. Each must pass the existing conformance suite against a live
instance before it may be called Supported; until then it is Experimental.

**Tracked as:** Phase 03 follow-up, blocking the Gate G2 "all three secret
stores certified" criterion. Managed identity is mandatory for the Azure
adapter — a connection string in configuration would defeat the entire phase.
