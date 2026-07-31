using System.Diagnostics;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Correlation;
using Edpf.Diagnostics;
using Serilog.Context;

namespace Edpf.WalkingSkeleton.Api.Pipeline;

/// <summary>
/// ADR-012 stage 1: correlation-id assignment. Continues an inbound
/// <c>X-Correlation-Id</c> or starts a fresh one; the id rides the async
/// context, the current trace, every log entry, and the response header —
/// one id from request to response (Phase 02 demonstration 3/8).
/// </summary>
public sealed class CorrelationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ICorrelationContextAccessor accessor)
    {
        string? inbound = context.Request.Headers[EdpfDiagnosticNames.CorrelationHeader].FirstOrDefault();
        CorrelationContext correlation = string.IsNullOrWhiteSpace(inbound)
            ? CorrelationContext.StartNew()
            : CorrelationContext.Continue(inbound);

        context.Response.Headers[EdpfDiagnosticNames.CorrelationHeader] = correlation.CorrelationId;
        Activity.Current?.SetTag("edpf.correlation_id", correlation.CorrelationId);

        using (accessor.Push(correlation))
        using (LogContext.PushProperty(LogFields.CorrelationId, correlation.CorrelationId))
        using (LogContext.PushProperty(LogFields.RequestId, correlation.RequestId))
        {
            await next(context);
        }
    }
}
