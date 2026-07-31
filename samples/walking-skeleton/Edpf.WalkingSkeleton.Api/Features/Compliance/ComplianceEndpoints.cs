using Edpf.Abstractions.Audit;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Security;
using Edpf.Abstractions.Tenancy;
using Edpf.WalkingSkeleton.Api.Features.Patients;
using Edpf.WalkingSkeleton.Api.Pipeline;

namespace Edpf.WalkingSkeleton.Api.Features.Compliance;

/// <summary>
/// The erasure and evidence endpoints of the gate demonstration (Phase 02
/// §⑤ items 4 and 6): crypto-shredding a subject, and verifying the audit
/// chain — including after the shred.
/// </summary>
public static class ComplianceEndpoints
{
    /// <summary>Maps <c>/api/v1/subjects/{id}/erase</c> and <c>/api/v1/audit/verify</c>.</summary>
    public static IEndpointRouteBuilder MapComplianceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/subjects/{id:guid}/erase", EraseAsync)
            .RequireAuthorization(AuthPolicies.ComplianceErase);

        app.MapGet("/api/v1/audit/verify", VerifyAsync)
            .RequireAuthorization(AuthPolicies.PatientsRead);

        return app;
    }

    private static async Task<IResult> EraseAsync(
        Guid id,
        IKeyManagementService kms,
        IAuditWriter auditWriter,
        ITenantContextAccessor tenantAccessor,
        ICorrelationContextAccessor correlation,
        CancellationToken cancellationToken)
    {
        ITenantContext tenant = tenantAccessor.Current!;

        Result destroyed = await kms.DestroyAsync(
            KeyScope.ForSubject(tenant.TenantId, id), cancellationToken);
        if (destroyed.IsFailure)
        {
            return Problems.ToResult(destroyed.Error!, correlation.Current);
        }

        // The erasure itself is audited (ADR-006): the record references the
        // subject by token, so it survives the very erasure it documents.
        Result audit = await auditWriter.WriteAsync(
            new AuditEventDescriptor(
                "ErasureCompleted", id, correlation.Current?.CorrelationId ?? "none"),
            cancellationToken);
        if (audit.IsFailure)
        {
            return Problems.ToResult(audit.Error!, correlation.Current);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> VerifyAsync(
        IAuditChainVerifier verifier,
        ITenantContextAccessor tenantAccessor,
        ICorrelationContextAccessor correlation,
        CancellationToken cancellationToken)
    {
        ITenantContext tenant = tenantAccessor.Current!;

        Result<AuditChainVerification> verification =
            await verifier.VerifyAsync(tenant.TenantId, cancellationToken);

        return verification.Match(
            outcome => Results.Ok(new
            {
                outcome.IsValid,
                outcome.RecordCount,
                outcome.FirstBrokenSequence,
            }),
            error => Problems.ToResult(error, correlation.Current));
    }
}
