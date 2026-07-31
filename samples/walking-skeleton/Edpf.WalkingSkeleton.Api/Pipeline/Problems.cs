using Edpf.Abstractions.Primitives;
using Microsoft.AspNetCore.Mvc;

namespace Edpf.WalkingSkeleton.Api.Pipeline;

/// <summary>
/// RFC 9457 Problem Details mapping (ADR-012 response stage; §10.2). The
/// stable EDPF code becomes the <c>type</c> URI; the correlation id rides an
/// extension so support can find the full story in the logs; detail never
/// exceeds what the error catalogue permits for that code.
/// </summary>
public static class Problems
{
    /// <summary>Base URI for error-type documentation.</summary>
    public const string TypeBaseUri = "https://errors.edpf.dev/";

    /// <summary>Maps an <see cref="Error"/> to its HTTP status per §10.2.</summary>
    public static int StatusFor(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Code switch
        {
            ErrorCodes.KeyDestroyed => StatusCodes.Status410Gone,
            ErrorCodes.RateLimited => StatusCodes.Status429TooManyRequests,
            ErrorCodes.CapabilityNotSupported => StatusCodes.Status501NotImplemented,
            _ => error.Category switch
            {
                ErrorCategory.Validation => StatusCodes.Status400BadRequest,
                ErrorCategory.Authentication => StatusCodes.Status401Unauthorized,
                ErrorCategory.Authorization => StatusCodes.Status403Forbidden,
                ErrorCategory.NotFound => StatusCodes.Status404NotFound,
                ErrorCategory.Conflict => StatusCodes.Status409Conflict,
                ErrorCategory.Concurrency => StatusCodes.Status409Conflict,
                ErrorCategory.Transient => StatusCodes.Status503ServiceUnavailable,
                ErrorCategory.Integration => StatusCodes.Status502BadGateway,
                _ => StatusCodes.Status500InternalServerError,
            },
        };
    }

    /// <summary>Builds the RFC 9457 document for an error.</summary>
    public static ProblemDetails ToProblemDetails(Error error, string? correlationId)
    {
        ArgumentNullException.ThrowIfNull(error);
        var problem = new ProblemDetails
        {
            Type = TypeBaseUri + error.Code,
            Title = error.Code,
            Status = StatusFor(error),
            Detail = error.Message,
        };
        problem.Extensions["errorCode"] = error.Code;
        problem.Extensions["correlationId"] = correlationId ?? error.CorrelationId;
        return problem;
    }

    /// <summary>Minimal-API result for a failed <see cref="Result"/>.</summary>
    public static IResult ToResult(Error error, ICorrelationContext? correlation)
    {
        ArgumentNullException.ThrowIfNull(error);
        ProblemDetails problem = ToProblemDetails(error, correlation?.CorrelationId);
        return Microsoft.AspNetCore.Http.Results.Problem(
            detail: problem.Detail,
            statusCode: problem.Status,
            title: problem.Title,
            type: problem.Type,
            extensions: problem.Extensions);
    }

    /// <summary>Writes the problem document directly (middleware path).</summary>
    public static async Task WriteAsync(HttpContext context, Error error)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(error);

        string? correlationId = context.Response.Headers[Edpf.Diagnostics.EdpfDiagnosticNames.CorrelationHeader]
            .FirstOrDefault();
        ProblemDetails problem = ToProblemDetails(error, correlationId);

        context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(problem, context.RequestAborted);
    }
}
