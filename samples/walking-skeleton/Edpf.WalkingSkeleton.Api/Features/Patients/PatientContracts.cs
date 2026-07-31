using Edpf.Abstractions.Primitives;
using FluentValidation;

namespace Edpf.WalkingSkeleton.Api.Features.Patients;

/// <summary>Create-patient request body.</summary>
public sealed record CreatePatientRequest(
    string GivenName,
    string FamilyName,
    DateOnly DateOfBirth,
    string MedicalRecordNumber);

/// <summary>Patient representation returned to authorized callers.</summary>
public sealed record PatientResponse(
    Guid Id,
    string GivenName,
    string FamilyName,
    DateOnly DateOfBirth,
    string MedicalRecordNumber,
    DateTimeOffset CreatedUtc);

/// <summary>Paged list envelope.</summary>
public sealed record PatientListResponse(
    IReadOnlyList<PatientResponse> Items,
    int PageNumber,
    int PageSize,
    long TotalCount);

/// <summary>
/// ADR-012 stage 5 rules. Failure messages name field and rule, never echo
/// the submitted value (§10.2 EDPF-VAL-1001).
/// </summary>
public sealed class CreatePatientRequestValidator : AbstractValidator<CreatePatientRequest>
{
    public CreatePatientRequestValidator(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        RuleFor(r => r.GivenName)
            .NotEmpty().WithMessage("required")
            .MaximumLength(128).WithMessage("too long");

        RuleFor(r => r.FamilyName)
            .NotEmpty().WithMessage("required")
            .MaximumLength(128).WithMessage("too long");

        RuleFor(r => r.DateOfBirth)
            .Must(d => d > new DateOnly(1900, 1, 1)).WithMessage("out of range")
            .Must(d => d <= DateOnly.FromDateTime(clock.UtcNow.UtcDateTime)).WithMessage("must not be in the future");

        RuleFor(r => r.MedicalRecordNumber)
            .NotEmpty().WithMessage("required")
            .Matches("^[A-Za-z0-9-]{4,64}$").WithMessage("must be 4-64 alphanumeric or dash characters");
    }
}
