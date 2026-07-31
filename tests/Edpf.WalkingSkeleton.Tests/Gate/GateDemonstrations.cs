using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Edpf.Diagnostics;
using Edpf.WalkingSkeleton.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Edpf.WalkingSkeleton.Tests.Gate;

/// <summary>
/// The Phase 02 §⑤ gate demonstrations, run live against a real provider.
/// Both Tier A providers execute the identical suite (demonstration 9 is the
/// pair of concrete classes at the bottom of this file).
/// </summary>
[Trait("Category", "RequiresDocker")]
public abstract class GateDemonstrations(ProviderFixture fixture)
{
    private static readonly Guid TenantA = SkeletonSeeder.TenantA;
    private static readonly Guid TenantB = SkeletonSeeder.TenantB;

    // ── helpers ────────────────────────────────────────────────────────────

    private async Task<HttpClient> ClientAsync(Guid tenant, string roles = "clinician")
    {
        HttpClient client = fixture.Factory.CreateClient();

        using HttpResponseMessage tokenResponse =
            await client.GetAsync(new Uri($"/dev/token?roles={roles}", UriKind.Relative));
        tokenResponse.EnsureSuccessStatusCode();
        JsonDocument payload = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        string token = payload.RootElement.GetProperty("token").GetString()!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add(EdpfDiagnosticNames.TenantHeader, tenant.ToString("D"));
        return client;
    }

    private static StringContent Body(object payload) => new(
        JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static object NewPatient(string mrn) => new
    {
        givenName = "Asha",
        familyName = "Verma",
        dateOfBirth = "1984-03-14",
        medicalRecordNumber = mrn,
    };

    private static async Task<(Guid Id, string Mrn)> CreatePatientAsync(HttpClient client)
    {
        string mrn = "MRN-" + Guid.NewGuid().ToString("N")[..10];
        using HttpResponseMessage response = await client.PostAsync(
            new Uri("/api/v1/patients", UriKind.Relative), Body(NewPatient(mrn)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (body.RootElement.GetProperty("id").GetGuid(), mrn);
    }

    // ── the demonstrations ─────────────────────────────────────────────────

    [Fact] // Demonstration 1
    public async Task Demo1_AuthenticatedCreate_Returns201WithLocation()
    {
        HttpClient client = await ClientAsync(TenantA);

        using HttpResponseMessage response = await client.PostAsync(
            new Uri("/api/v1/patients", UriKind.Relative), Body(NewPatient("MRN-DEMO-0001")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
    }

    [Fact] // Demonstration 2 — the isolation case: 404, never 403
    public async Task Demo2_TenantB_ReadingTenantAPatient_Gets404Not403()
    {
        HttpClient clientA = await ClientAsync(TenantA);
        (Guid patientId, _) = await CreatePatientAsync(clientA);

        HttpClient clientB = await ClientAsync(TenantB); // fully authorized, wrong tenant
        using HttpResponseMessage response =
            await clientB.GetAsync(new Uri($"/api/v1/patients/{patientId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact] // Demonstration 3 — one correlation id through the stack
    public async Task Demo3_SuppliedCorrelationId_RoundTripsOnResponse()
    {
        HttpClient client = await ClientAsync(TenantA);
        string correlationId = Guid.NewGuid().ToString("N");
        client.DefaultRequestHeaders.Add(EdpfDiagnosticNames.CorrelationHeader, correlationId);

        using HttpResponseMessage response =
            await client.GetAsync(new Uri("/api/v1/patients?page=1&pageSize=5", UriKind.Relative));

        Assert.Equal(correlationId,
            response.Headers.GetValues(EdpfDiagnosticNames.CorrelationHeader).Single());
    }

    [Fact] // Demonstration 4 — audit exists, chain verifies, carries no raw PHI
    public async Task Demo4_AuditChain_VerifiesAndHoldsNoRawPhi()
    {
        HttpClient client = await ClientAsync(TenantA);
        (Guid patientId, string mrn) = await CreatePatientAsync(client);

        using HttpResponseMessage verify =
            await client.GetAsync(new Uri("/api/v1/audit/verify", UriKind.Relative));
        JsonDocument verification = JsonDocument.Parse(await verify.Content.ReadAsStringAsync());

        Assert.True(verification.RootElement.GetProperty("isValid").GetBoolean());
        Assert.True(verification.RootElement.GetProperty("recordCount").GetInt64() > 0);

        await using SkeletonDbContext db = fixture.CreateDirectDbContext(new FixedTenantAccessor(TenantA));
        List<AuditRow> rows = await db.AuditEvents.AsNoTracking().ToListAsync();
        Assert.All(rows, row =>
        {
            Assert.DoesNotContain(mrn, row.SubjectToken, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(patientId.ToString("D"), row.SubjectToken, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact] // Demonstration 5 — the PHI field is ciphertext in the raw table
    public async Task Demo5_RawDatabase_HoldsCiphertextNotPlaintext()
    {
        HttpClient client = await ClientAsync(TenantA);
        (Guid patientId, string mrn) = await CreatePatientAsync(client);

        await using SkeletonDbContext db = fixture.CreateDirectDbContext(new FixedTenantAccessor(TenantA));
        PatientRow row = await db.Patients.AsNoTracking()
            .IgnoreQueryFilters()
            .SingleAsync(p => p.Id == patientId);

        byte[] plaintextBytes = Encoding.UTF8.GetBytes(mrn);
        Assert.True(row.MrnEnvelope.Length > plaintextBytes.Length);
        Assert.DoesNotContain(mrn, Convert.ToHexString(row.MrnEnvelope), StringComparison.OrdinalIgnoreCase);
        Assert.False(ContainsSubsequence(row.MrnEnvelope, plaintextBytes),
            "Raw table must never contain the plaintext MRN bytes.");
    }

    [Fact] // Demonstration 6 — crypto-shredding: data gone, chain intact
    public async Task Demo6_Erasure_MakesDataUnrecoverable_ChainStillValid()
    {
        HttpClient clinician = await ClientAsync(TenantA);
        (Guid patientId, _) = await CreatePatientAsync(clinician);

        HttpClient officer = await ClientAsync(TenantA, roles: "compliance-officer");
        using HttpResponseMessage erase = await officer.PostAsync(
            new Uri($"/api/v1/subjects/{patientId}/erase", UriKind.Relative), content: null);
        Assert.Equal(HttpStatusCode.NoContent, erase.StatusCode);

        using HttpResponseMessage read =
            await clinician.GetAsync(new Uri($"/api/v1/patients/{patientId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        JsonDocument patient = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal("[erased]", patient.RootElement.GetProperty("medicalRecordNumber").GetString());

        using HttpResponseMessage verify =
            await clinician.GetAsync(new Uri("/api/v1/audit/verify", UriKind.Relative));
        JsonDocument verification = JsonDocument.Parse(await verify.Content.ReadAsStringAsync());
        Assert.True(verification.RootElement.GetProperty("isValid").GetBoolean());
    }

    [Fact] // Demonstration 7 — outbox message present, dispatched exactly once
    public async Task Demo7_OutboxMessage_DispatchedExactlyOnce()
    {
        HttpClient client = await ClientAsync(TenantA);
        (Guid patientId, _) = await CreatePatientAsync(client);

        await using SkeletonDbContext db = fixture.CreateDirectDbContext(new FixedTenantAccessor(TenantA));

        OutboxRow? dispatched = null;
        for (int i = 0; i < 30 && dispatched is null; i++)
        {
            await Task.Delay(500);
            dispatched = await db.Outbox.AsNoTracking()
                .Where(o => o.MessageType == "PatientCreated"
                    && o.Payload.Contains(patientId.ToString("D"))
                    && o.DispatchedUtc != null)
                .SingleOrDefaultAsync();
        }

        Assert.NotNull(dispatched);
        Assert.Equal(1, dispatched.Attempts);

        int totalForPatient = await db.Outbox.AsNoTracking()
            .CountAsync(o => o.Payload.Contains(patientId.ToString("D")));
        Assert.Equal(1, totalForPatient);
    }

    [Fact] // Demonstration 8 — a forced failure is a well-formed RFC 9457 document
    public async Task Demo8_ValidationFailure_YieldsProblemDetailsWithCorrelationId()
    {
        HttpClient client = await ClientAsync(TenantA);

        using HttpResponseMessage response = await client.PostAsync(
            new Uri("/api/v1/patients", UriKind.Relative),
            Body(new { givenName = "", familyName = "", dateOfBirth = "1990-01-01", medicalRecordNumber = "x" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("EDPF-VAL-1001", problem.RootElement.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrEmpty(problem.RootElement.GetProperty("correlationId").GetString()));
        Assert.StartsWith("https://errors.edpf.dev/", problem.RootElement.GetProperty("type").GetString());
    }

    [Fact] // Idempotency (ADR-012 stage 6): replay and conflict semantics
    public async Task Idempotency_SameKeySamePayloadReplays_DifferentPayloadConflicts()
    {
        HttpClient client = await ClientAsync(TenantA);
        string key = Guid.NewGuid().ToString("N");
        object payload = NewPatient("MRN-IDEM-001");

        using var first = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/patients", UriKind.Relative))
        { Content = Body(payload) };
        first.Headers.Add(EdpfDiagnosticNames.IdempotencyKeyHeader, key);
        using HttpResponseMessage firstResponse = await client.SendAsync(first);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        string firstBody = await firstResponse.Content.ReadAsStringAsync();

        using var replay = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/patients", UriKind.Relative))
        { Content = Body(payload) };
        replay.Headers.Add(EdpfDiagnosticNames.IdempotencyKeyHeader, key);
        using HttpResponseMessage replayResponse = await client.SendAsync(replay);
        Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
        Assert.Equal(
            JsonDocument.Parse(firstBody).RootElement.GetProperty("id").GetGuid(),
            JsonDocument.Parse(await replayResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid());

        using var conflict = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/patients", UriKind.Relative))
        { Content = Body(NewPatient("MRN-IDEM-002")) };
        conflict.Headers.Add(EdpfDiagnosticNames.IdempotencyKeyHeader, key);
        using HttpResponseMessage conflictResponse = await client.SendAsync(conflict);
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
    }

    [Fact] // AuthN/AuthZ negatives: 401 without a token, 403 without the role
    public async Task Auth_MissingToken401_MissingRole403()
    {
        HttpClient anonymous = fixture.Factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add(EdpfDiagnosticNames.TenantHeader, TenantA.ToString("D"));
        using HttpResponseMessage unauthenticated =
            await anonymous.GetAsync(new Uri("/api/v1/patients", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        HttpClient clinician = await ClientAsync(TenantA); // clinician cannot erase
        using HttpResponseMessage forbidden = await clinician.PostAsync(
            new Uri($"/api/v1/subjects/{Guid.NewGuid()}/erase", UriKind.Relative), content: null);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact] // ADR-012: the composed pipeline order is exactly the canonical order
    public async Task Pipeline_ComposedOrder_MatchesAdr012Canonical()
    {
        // Booting the factory composes the pipeline; the recorded order must
        // match the ADR verbatim (Phase 02 exit criterion).
        _ = await ClientAsync(TenantA);

        Assert.Equal(
            Edpf.WalkingSkeleton.Api.Pipeline.PipelineStages.CanonicalOrder,
            Edpf.WalkingSkeleton.Api.Pipeline.PipelineStages.ComposedOrder);
    }

    [Fact] // Paged list behaves at boundaries
    public async Task List_Paged_ReturnsPageMetadata()
    {
        HttpClient client = await ClientAsync(TenantA);
        await CreatePatientAsync(client);

        using HttpResponseMessage response =
            await client.GetAsync(new Uri("/api/v1/patients?page=1&pageSize=2", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("pageNumber").GetInt32());
        Assert.Equal(2, body.RootElement.GetProperty("pageSize").GetInt32());
        Assert.True(body.RootElement.GetProperty("totalCount").GetInt64() >= 1);
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Demonstration 9a: the identical suite on SQL Server.</summary>
public sealed class SqlServerGateDemonstrations(SqlServerFixture fixture)
    : GateDemonstrations(fixture), IClassFixture<SqlServerFixture>;

/// <summary>Demonstration 9b: the identical suite on PostgreSQL.</summary>
public sealed class PostgreSqlGateDemonstrations(PostgreSqlFixture fixture)
    : GateDemonstrations(fixture), IClassFixture<PostgreSqlFixture>;
