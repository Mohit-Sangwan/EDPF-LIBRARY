using System.Security.Cryptography;
using System.Text;
using Edpf.Abstractions.Consistency;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Tenancy;
using Edpf.Diagnostics;

namespace Edpf.WalkingSkeleton.Api.Pipeline;

/// <summary>
/// ADR-012 stage 6: idempotency, after validation, before the handler.
/// An <c>Idempotency-Key</c> replayed with the same payload returns the stored
/// response; the same key with a different payload is EDPF-TX-4002 (409).
/// The key is optional in the skeleton; Phase 09 makes it mandatory for
/// mutating endpoints.
/// </summary>
public sealed class IdempotencyFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        HttpContext http = context.HttpContext;
        string? key = http.Request.Headers[EdpfDiagnosticNames.IdempotencyKeyHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(key))
        {
            return await next(context);
        }

        var tenantAccessor = http.RequestServices.GetRequiredService<ITenantContextAccessor>();
        var correlationAccessor = http.RequestServices.GetRequiredService<ICorrelationContextAccessor>();
        ITenantContext tenant = tenantAccessor.Current
            ?? throw new InvalidOperationException("Idempotency stage reached without a resolved tenant (ADR-012).");

        var store = http.RequestServices.GetRequiredService<IIdempotencyStore>();
        string requestHash = HashRequestBody(context);

        IdempotencyRecord? existing = await store.FindAsync(tenant.TenantId, key, http.RequestAborted);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            {
                return Problems.ToResult(
                    new Error(
                        ErrorCodes.IdempotencyConflict,
                        "The idempotency key was already used with a different payload.",
                        ErrorCategory.Conflict),
                    correlationAccessor.Current);
            }

            // Exact replay: return the original outcome without re-executing.
            return Microsoft.AspNetCore.Http.Results.Text(
                existing.ResponseBody, "application/json", statusCode: existing.ResponseStatusCode);
        }

        object? outcome = await next(context);

        if (outcome is IValueHttpResult { Value: not null } valueResult
            && outcome is IStatusCodeHttpResult { StatusCode: not null } statusResult)
        {
            var clock = http.RequestServices.GetRequiredService<IClock>();
            string body = System.Text.Json.JsonSerializer.Serialize(valueResult.Value);
            await store.SaveAsync(
                new IdempotencyRecord(
                    tenant.TenantId, key, requestHash, statusResult.StatusCode.Value, body, clock.UtcNow),
                http.RequestAborted);
        }

        return outcome;
    }

    private static string HashRequestBody(EndpointFilterInvocationContext context)
    {
        // The bound request DTO is the canonical payload: header noise and
        // formatting differences do not defeat replay detection.
        object? dto = context.Arguments.FirstOrDefault(a => a is not null && a.GetType().IsClass && a is not HttpContext);
        string json = System.Text.Json.JsonSerializer.Serialize(dto);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
