# Phase 02 — Running the walking skeleton

## Option A — full harness (Docker)

```bash
docker compose -f samples/walking-skeleton/docker-compose.yml up -d
```

Brings up SQL Server (`localhost:14331`), PostgreSQL (`localhost:54321`),
Jaeger (UI at `http://localhost:16686`) and Seq (UI at `http://localhost:5341`).

```bash
dotnet run --project samples/walking-skeleton/Edpf.WalkingSkeleton.Api --framework net10.0
```

Switch providers without touching code:

```bash
dotnet run --project samples/walking-skeleton/Edpf.WalkingSkeleton.Api --framework net10.0 -- --Database:Provider=PostgreSql
```

## Option B — no Docker (SQL Server LocalDB)

```powershell
$env:ConnectionStrings__SqlServer = "Server=(localdb)\MSSQLLocalDB;Database=EdpfSkeletonDemo;Trusted_Connection=True;TrustServerCertificate=True"
$env:OTEL_SDK_DISABLED = "true"
dotnet run --project samples/walking-skeleton/Edpf.WalkingSkeleton.Api --framework net10.0
```

## Run the gate demonstration

```powershell
./samples/walking-skeleton/gate-demonstration.ps1
```

Prints pass/fail for all ten demonstrations plus the idempotency and
authorization negatives.

## By hand

Mint a development token (Development environment only):

```bash
curl "http://localhost:5080/dev/token?roles=clinician"
```

Create a patient under tenant A — note the two required headers:

```bash
curl -X POST http://localhost:5080/api/v1/patients -H "Authorization: Bearer $TOKEN" -H "X-Tenant-Id: aaaaaaaa-0000-0000-0000-000000000001" -H "Content-Type: application/json" -d '{"givenName":"Asha","familyName":"Verma","dateOfBirth":"1984-03-14","medicalRecordNumber":"MRN-000123"}'
```

Verify the audit chain:

```bash
curl http://localhost:5080/api/v1/audit/verify -H "Authorization: Bearer $TOKEN" -H "X-Tenant-Id: aaaaaaaa-0000-0000-0000-000000000001"
```

Erase a subject — requires the `compliance-officer` role:

```bash
curl -X POST http://localhost:5080/api/v1/subjects/$PATIENT_ID/erase -H "Authorization: Bearer $OFFICER_TOKEN" -H "X-Tenant-Id: aaaaaaaa-0000-0000-0000-000000000001"
```

## Seeded tenants

| Tenant | Id | Region |
|---|---|---|
| Aurora Health | `aaaaaaaa-0000-0000-0000-000000000001` | `in-south-1` |
| Borealis Clinic | `bbbbbbbb-0000-0000-0000-000000000002` | `eu-central-1` |

Two tenants exist so the isolation demonstration is always available: create
under Aurora, read as Borealis, receive 404.

## Endpoints

| Method | Route | Policy |
|---|---|---|
| `POST` | `/api/v1/patients` | `patients:write` (clinician, admin) |
| `GET` | `/api/v1/patients/{id}` | `patients:read` |
| `GET` | `/api/v1/patients?page=&pageSize=` | `patients:read` |
| `POST` | `/api/v1/subjects/{id}/erase` | `compliance:erase` (compliance-officer) |
| `GET` | `/api/v1/audit/verify` | `patients:read` |
| `GET` | `/health` | anonymous, tenant-exempt |
| `GET` | `/dev/token` | Development only |
