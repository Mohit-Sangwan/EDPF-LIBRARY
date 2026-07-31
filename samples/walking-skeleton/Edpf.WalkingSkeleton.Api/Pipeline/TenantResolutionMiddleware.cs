using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Tenancy;
using Edpf.Diagnostics;
using Serilog.Context;

namespace Edpf.WalkingSkeleton.Api.Pipeline;

/// <summary>
/// ADR-012 stage 2: tenant resolution (C4 §12.6), before authentication and
/// always before any data access. An absent, malformed or unknown tenant key
/// yields 404 problem details — existence is never disclosed (EDPF-AUTHZ-2102).
/// Infrastructure endpoints (health, dev token minting) are tenant-exempt by
/// explicit allow-list, not by fall-through.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    private static readonly string[] ExemptPrefixes = ["/health", "/dev"];

    public async Task InvokeAsync(
        HttpContext context,
        ITenantResolver resolver,
        ITenantContextAccessor accessor)
    {
        if (IsExempt(context.Request.Path))
        {
            await next(context);
            return;
        }

        string? key = context.Request.Headers[EdpfDiagnosticNames.TenantHeader].FirstOrDefault();
        Result<TenantDescriptor> resolved = string.IsNullOrWhiteSpace(key)
            ? Result.Failure<TenantDescriptor>(new Error(
                ErrorCodes.TenantScopeViolation,
                "The requested resource was not found.",
                ErrorCategory.NotFound))
            : await resolver.ResolveAsync(key, context.RequestAborted);

        if (resolved.IsFailure)
        {
            await Problems.WriteAsync(context, resolved.Error!);
            return;
        }

        using (accessor.Push(resolved.Value))
        using (LogContext.PushProperty(LogFields.TenantId, resolved.Value.TenantId))
        {
            await next(context);
        }
    }

    private static bool IsExempt(PathString path)
        => ExemptPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
}
