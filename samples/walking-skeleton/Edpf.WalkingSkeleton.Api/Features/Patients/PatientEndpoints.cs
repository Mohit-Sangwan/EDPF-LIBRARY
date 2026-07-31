using Edpf.Abstractions.Data;
using Edpf.Abstractions.Primitives;
using Edpf.WalkingSkeleton.Api.Domain;
using Edpf.WalkingSkeleton.Api.Pipeline;

namespace Edpf.WalkingSkeleton.Api.Features.Patients;

/// <summary>
/// The skeleton's operation set (Phase 02 §③): create, read-by-id, paged
/// list. URI-versioned per Z.14. Authorization is policy-based RBAC; every
/// failure is an RFC 9457 document.
/// </summary>
public static class PatientEndpoints
{
    /// <summary>Maps <c>/api/v1/patients</c>.</summary>
    public static IEndpointRouteBuilder MapPatientEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/v1/patients");

        group.MapPost("/", CreateAsync)
            .RequireAuthorization(AuthPolicies.PatientsWrite)
            .AddEndpointFilter<ValidationFilter<CreatePatientRequest>>()
            .AddEndpointFilter<IdempotencyFilter>();

        group.MapGet("/{id:guid}", GetByIdAsync)
            .RequireAuthorization(AuthPolicies.PatientsRead);

        group.MapGet("/", ListAsync)
            .RequireAuthorization(AuthPolicies.PatientsRead);

        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreatePatientRequest request,
        IRepository<Patient, Guid> repository,
        ICorrelationContextAccessor correlation,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var patient = new Patient
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.Empty, // stamped by the repository from the resolved tenant
            GivenName = request.GivenName,
            FamilyName = request.FamilyName,
            DateOfBirth = request.DateOfBirth,
            MedicalRecordNumber = request.MedicalRecordNumber,
            CreatedUtc = clock.UtcNow,
        };

        Result<Patient> result = await repository.AddAsync(patient, cancellationToken);
        return result.Match(
            created => Results.Created($"/api/v1/patients/{created.Id}", ToResponse(created)),
            error => Problems.ToResult(error, correlation.Current));
    }

    private static async Task<IResult> GetByIdAsync(
        Guid id,
        IRepository<Patient, Guid> repository,
        ICorrelationContextAccessor correlation,
        CancellationToken cancellationToken)
    {
        Result<Patient> result = await repository.GetByIdAsync(id, cancellationToken);
        return result.Match(
            patient => Results.Ok(ToResponse(patient)),
            error => Problems.ToResult(error, correlation.Current));
    }

    private static async Task<IResult> ListAsync(
        IRepository<Patient, Guid> repository,
        ICorrelationContextAccessor correlation,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = PageRequest.DefaultPageSize)
    {
        if (page < 1 || pageSize < 1 || pageSize > PageRequest.MaxPageSize)
        {
            return Problems.ToResult(
                new Error(
                    ErrorCodes.ValidationFailed,
                    $"page must be >= 1 and pageSize between 1 and {PageRequest.MaxPageSize}.",
                    ErrorCategory.Validation),
                correlation.Current);
        }

        Result<PagedResult<Patient>> result =
            await repository.ListAsync(new PageRequest(page, pageSize), cancellationToken);

        return result.Match(
            paged => Results.Ok(new PatientListResponse(
                paged.Items.Select(ToResponse).ToList(),
                paged.PageNumber,
                paged.PageSize,
                paged.TotalCount)),
            error => Problems.ToResult(error, correlation.Current));
    }

    private static PatientResponse ToResponse(Patient patient) => new(
        patient.Id,
        patient.GivenName,
        patient.FamilyName,
        patient.DateOfBirth,
        patient.MedicalRecordNumber,
        patient.CreatedUtc);
}

/// <summary>The skeleton's RBAC policies (one policy set, ADR-012 stage 4).</summary>
public static class AuthPolicies
{
    public const string PatientsRead = "patients:read";
    public const string PatientsWrite = "patients:write";
    public const string ComplianceErase = "compliance:erase";

    /// <summary>Roles understood by the skeleton.</summary>
    public static class Roles
    {
        public const string Clinician = "clinician";
        public const string Admin = "admin";
        public const string ComplianceOfficer = "compliance-officer";
    }
}
