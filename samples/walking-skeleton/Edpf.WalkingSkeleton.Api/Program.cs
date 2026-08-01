using Edpf.Abstractions.Audit;
using Edpf.Abstractions.Configuration;
using Edpf.Abstractions.Consistency;
using Edpf.Abstractions.Data;
using Edpf.Abstractions.Diagnostics;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Security;
using Edpf.Abstractions.Tenancy;
using Edpf.Configuration.Secrets;
using Edpf.Diagnostics;
using Edpf.Diagnostics.Metrics;
using Edpf.Diagnostics.Redaction;
using Edpf.Extensions.DependencyInjection;
using Edpf.Extensions.DependencyInjection.Validation;
using Edpf.WalkingSkeleton.Api.Domain;
using Edpf.WalkingSkeleton.Api.Features.Compliance;
using Edpf.WalkingSkeleton.Api.Features.Dev;
using Edpf.WalkingSkeleton.Api.Features.Patients;
using Edpf.WalkingSkeleton.Api.Infrastructure.Audit;
using Edpf.WalkingSkeleton.Api.Infrastructure.Auth;
using Edpf.WalkingSkeleton.Api.Infrastructure.Consistency;
using Edpf.WalkingSkeleton.Api.Infrastructure.Persistence;
using Edpf.WalkingSkeleton.Api.Infrastructure.Security;
using Edpf.WalkingSkeleton.Api.Infrastructure.Tenancy;
using Edpf.WalkingSkeleton.Api.Pipeline;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ── Logging: Serilog with the standard schema (Phase 00 §⑥) ────────────────
builder.Host.UseSerilog((context, services, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext());

// ── Configuration objects ──────────────────────────────────────────────────
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<KeyManagementOptions>(
    builder.Configuration.GetSection(KeyManagementOptions.SectionName));

// Fail at startup, never mid-request (EDPF-CFG-8001): production requires an
// explicit signing key; Development may run on the ephemeral one.
if (!builder.Environment.IsDevelopment()
    && string.IsNullOrEmpty(builder.Configuration[$"{JwtOptions.SectionName}:SigningKeyBase64"]))
{
    throw new InvalidOperationException(
        $"{ErrorCodes.ConfigurationInvalid}: Jwt:SigningKeyBase64 must be configured outside Development.");
}

// ── Shared kernel via the Phase 04 composition root (ADR-014) ─────────────
// One feature-module call replaces the hand-wired kernel registrations; the
// lifetime policy now lives in one place instead of per host.
builder.Services.AddEdpfCore(builder.Configuration);

// ── Secret custody (Phase 03, ADR-013) ────────────────────────────────────
// Environment first (the orchestrator's injected material), then an
// in-memory layer the dev harness can seed. Production layers a vault store
// on top of the same chain without any consumer changing.
builder.Services.AddSingleton<ISecretStore>(sp => new ChainedSecretStore(
[
    new EnvironmentSecretStore(),
    new InMemorySecretStore(sp.GetRequiredService<IClock>()),
]));

// ── Redaction (Phase 05, ADR-015) ─────────────────────────────────────────
builder.Services.AddSingleton<ISensitiveDataRedactor>(new SensitiveDataRedactor());
builder.Services.AddSingleton<EdpfMetrics>();

// ── Persistence: provider chosen by configuration (SQL Server | PostgreSQL) ─
string provider = builder.Configuration["Database:Provider"] ?? "SqlServer";
builder.Services.AddDbContext<SkeletonDbContext>(options =>
{
    string connectionString = builder.Configuration.GetConnectionString(provider)
        ?? throw new InvalidOperationException(
            $"{ErrorCodes.ConfigurationInvalid}: ConnectionStrings:{provider} is not configured.");

    if (string.Equals(provider, "PostgreSql", StringComparison.OrdinalIgnoreCase))
    {
        options.UseNpgsql(connectionString);
    }
    else
    {
        options.UseSqlServer(connectionString);
    }
});

// ── Security, audit, tenancy, consistency (skeleton implementations) ───────
builder.Services.AddSingleton<IAlgorithmRegistry, AlgorithmRegistry>();
builder.Services.AddSingleton<IHashingService, HashingService>();
builder.Services.AddScoped<KeyManagementService>();
builder.Services.AddScoped<IKeyManagementService>(sp => sp.GetRequiredService<KeyManagementService>());
builder.Services.AddScoped<Edpf.Abstractions.Security.ICryptoProvider, CryptoProvider>();
builder.Services.AddScoped<ITokenizer, SubjectTokenizer>();
builder.Services.AddScoped<IAuditWriter, AuditWriter>();
builder.Services.AddScoped<IAuditChainVerifier, AuditChainVerifier>();
builder.Services.AddScoped<ITenantStore, EfTenantStore>();
builder.Services.AddScoped<ITenantResolver, HeaderTenantResolver>();
builder.Services.AddScoped<IOutboxDispatcher, EfOutboxDispatcher>();
builder.Services.AddScoped<IIdempotencyStore, EfIdempotencyStore>();
builder.Services.AddScoped<IRepository<Patient, Guid>, PatientRepository>();
builder.Services.AddValidatorsFromAssemblyContaining<CreatePatientRequestValidator>();
builder.Services.AddHostedService<OutboxDispatcherService>();

// ── AuthN/AuthZ: JWT bearer + RBAC policies (ADR-012 stages 3-4) ───────────
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        JwtOptions jwt = builder.Configuration
            .GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(jwt.ResolveSigningKey()),
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthPolicies.PatientsRead, policy =>
        policy.RequireRole(AuthPolicies.Roles.Clinician, AuthPolicies.Roles.Admin))
    .AddPolicy(AuthPolicies.PatientsWrite, policy =>
        policy.RequireRole(AuthPolicies.Roles.Clinician, AuthPolicies.Roles.Admin))
    .AddPolicy(AuthPolicies.ComplianceErase, policy =>
        policy.RequireRole(AuthPolicies.Roles.ComplianceOfficer));

// ── Telemetry: OTel traces, OTLP export (Jaeger) ───────────────────────────
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("edpf-walking-skeleton"))
    .WithTracing(tracing => tracing
        .AddSource(EdpfDiagnosticNames.ActivitySourceName)
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter());

builder.Services.AddProblemDetails();

// Health checks, correctly differentiated (Phase 05 §④): liveness answers
// "is the process up", readiness "can it serve traffic". A dependency outage
// must drain this instance without killing it.
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddDbContextCheck<SkeletonDbContext>("database", tags: ["ready"]);

// ADR-014: the graph is swept for captive dependencies before the container
// is built, and the container validates scopes and the whole graph at boot —
// in every environment, not only Development.
CaptiveDependencyDetector.ThrowIfAny(builder.Services);
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});

var app = builder.Build();

// ═══ ADR-012: the fixed pipeline order. Do not reorder — the architecture
// test asserts this sequence against PipelineStages.CanonicalOrder. ═════════
PipelineStages.ComposedOrder.Clear();

app.UseMiddleware<UnhandledExceptionMiddleware>();        // Response (RFC 9457 on failure)
PipelineStages.ComposedOrder.Add(PipelineStages.Correlation);
app.UseMiddleware<CorrelationMiddleware>();               // 1. Correlation ID assignment
PipelineStages.ComposedOrder.Add(PipelineStages.TenantResolution);
app.UseMiddleware<TenantResolutionMiddleware>();          // 2. Tenant resolution
PipelineStages.ComposedOrder.Add(PipelineStages.Authentication);
app.UseAuthentication();                                  // 3. Authentication
PipelineStages.ComposedOrder.Add(PipelineStages.Authorization);
app.UseAuthorization();                                   // 4. Authorization
PipelineStages.ComposedOrder.Add(PipelineStages.Validation);   // 5. per-endpoint ValidationFilter
PipelineStages.ComposedOrder.Add(PipelineStages.Idempotency);  // 6. per-endpoint IdempotencyFilter
PipelineStages.ComposedOrder.Add(PipelineStages.Handler);      // 7. endpoint handlers
PipelineStages.ComposedOrder.Add(PipelineStages.Transaction);  // 8. repository transaction + outbox
PipelineStages.ComposedOrder.Add(PipelineStages.Audit);        // 9. audit emission (in-transaction)
PipelineStages.ComposedOrder.Add(PipelineStages.Telemetry);    // 10. OTel emission (automatic)
PipelineStages.ComposedOrder.Add(PipelineStages.Response);     // 11. response / problem details

// Liveness never touches a dependency: a database outage must not cause the
// orchestrator to restart a healthy process. Readiness does, so traffic drains.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live"),
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
});
app.MapHealthChecks("/health"); // aggregate, kept for the Phase 02 harness
app.MapPatientEndpoints();
app.MapComplianceEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapDevTokenEndpoint();

    // Skeleton-scale schema management: EnsureCreated + seed. Real migrations
    // are Phase 11 (ADR-005); recorded as TDL-0001.
    using IServiceScope scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<SkeletonDbContext>();
    await db.Database.EnsureCreatedAsync();
    await SkeletonSeeder.SeedAsync(db);
}

await app.RunAsync();

/// <summary>Exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program;
