using Edpf.Abstractions.Primitives;

namespace Edpf.WalkingSkeleton.Api.Pipeline;

/// <summary>
/// ADR-012 response stage: any unhandled exception becomes a well-formed
/// RFC 9457 document carrying the correlation id and nothing else — no stack,
/// no type name, no internals (Z.10). The full exception goes to the log,
/// keyed by the correlation id.
/// </summary>
public sealed class UnhandledExceptionMiddleware(
    RequestDelegate next,
    ILogger<UnhandledExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client went away; nothing to report.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "Unhandled exception");

            if (!context.Response.HasStarted)
            {
                await Problems.WriteAsync(context, new Error(
                    ErrorCodes.ProviderFailure,
                    "An unexpected error occurred. Quote the correlation id when reporting.",
                    ErrorCategory.Internal));
            }
        }
    }
}
