# Phase 00 — Reference architecture (C4 L1–L3)

## C4 L1 — System context

```mermaid
graph TB
    clinician["Clinician / Admin<br/>(hospital staff)"]
    patient["Patient<br/>(portal, DSAR requests)"]
    app["Consuming Application<br/>(HIS / EMR / LIS built on EDPF)"]
    edpf["<b>EDPF</b><br/>Enterprise Data Platform Framework<br/>compliance · tenancy · audit"]
    stores[("Data stores<br/>SQL Server · PostgreSQL · Oracle<br/>MongoDB · Redis · OpenSearch")]
    kms["Key custody<br/>Azure Key Vault / AWS KMS / HSM"]
    idp["Identity provider<br/>Entra ID / OIDC / SAML"]
    obs["Observability<br/>OTLP collector · log sink"]
    partners["Integration partners<br/>FHIR · HL7v2 · DICOM · ABDM"]

    clinician --> app
    patient --> app
    app --> edpf
    edpf --> stores
    edpf --> kms
    edpf --> idp
    edpf --> obs
    edpf --> partners
```

## C4 L2 — Containers (v0.9 scope)

```mermaid
graph TB
    subgraph host["Consuming host process"]
        api["API surface<br/>ASP.NET Core / Worker / Console"]
        pipeline["EDPF request pipeline<br/>(ADR-012, 11 fixed stages)"]
        domain["Application handlers"]
    end

    subgraph edpf["EDPF assemblies"]
        abs["Edpf.Abstractions<br/>contracts, zero deps"]
        core["Edpf.Core<br/>shared kernel"]
        compat["Edpf.Compatibility<br/>the only #if"]
        diag["Edpf.Diagnostics<br/>log schema, instrument names"]
        sec["Edpf.Security<br/>crypto, KMS, tokenizer"]
        aud["Edpf.Audit<br/>hash chain, verifier"]
        ten["Edpf.Tenancy<br/>resolution, isolation, keys"]
        data["Edpf.Data<br/>repository, UoW, outbox"]
    end

    subgraph providers["Providers (optional packages)"]
        pa["SqlServer · PostgreSql (Tier A)"]
        pb["Oracle · MySQL · MongoDB (Tier B/C)"]
    end

    stores[("Databases")]
    kms["Key custody"]

    api --> pipeline --> domain --> data
    pipeline --> ten
    pipeline --> sec
    data --> aud
    data --> providers
    sec --> kms
    providers --> stores

    core --> abs
    core --> compat
    sec --> abs
    aud --> abs
    ten --> abs
    data --> abs
    diag --> abs
```

## C4 L3 — Components on the write path

```mermaid
graph LR
    req["POST /patients"] --> corr["CorrelationMiddleware<br/>stage 1"]
    corr --> tenres["TenantResolutionMiddleware<br/>stage 2"]
    tenres --> authn["JwtBearer authentication<br/>stage 3"]
    authn --> authz["Policy authorization<br/>stage 4"]
    authz --> val["ValidationFilter<br/>stage 5"]
    val --> idem["IdempotencyFilter<br/>stage 6"]
    idem --> handler["Endpoint handler<br/>stage 7"]
    handler --> repo["Repository.AddAsync"]
    repo --> crypto["ICryptoProvider.Encrypt<br/>per-subject DEK"]
    crypto --> kms["IKeyManagementService"]
    repo --> tx["Local transaction<br/>stage 8"]
    tx --> row[("PATIENT row<br/>ciphertext MRN")]
    tx --> obx[("OUTBOX_MESSAGE")]
    tx --> audit["IAuditWriter<br/>stage 9"]
    audit --> chain[("AUDIT_EVENT<br/>hash-chained")]
    tx --> resp["201 Created<br/>stage 11"]
    obx -.dispatch.-> disp["OutboxDispatcherService"]
```

The write path above is not aspirational: it is the code path exercised by
the gate demonstrations in `tests/Edpf.WalkingSkeleton.Tests`, and the stage
order is asserted against ADR-012 by an architecture test.

Detailed code-level diagrams (C4 L4) for the six ADR-fixed paths live in the
master document Part 12; their conformance is asserted by
`DiagramConformanceTests`.
