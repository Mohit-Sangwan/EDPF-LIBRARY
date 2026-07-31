using Edpf.Abstractions.Primitives;
using FluentValidation;
using FluentValidation.Results;

namespace Edpf.WalkingSkeleton.Api.Pipeline;

/// <summary>
/// ADR-012 stage 5: validation, after authorization, before the handler.
/// Failures report field + rule, never the raw input (§10.2 EDPF-VAL-1001).
/// </summary>
public sealed class ValidationFilter<TRequest> : IEndpointFilter
    where TRequest : class
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        TRequest? request = context.Arguments.OfType<TRequest>().FirstOrDefault();
        IValidator<TRequest>? validator =
            context.HttpContext.RequestServices.GetService<IValidator<TRequest>>();

        if (request is not null && validator is not null)
        {
            ValidationResult validation =
                await validator.ValidateAsync(request, context.HttpContext.RequestAborted);

            if (!validation.IsValid)
            {
                string summary = string.Join(
                    "; ",
                    validation.Errors.Select(e => $"{e.PropertyName}: {e.ErrorMessage}"));

                var accessor = context.HttpContext.RequestServices
                    .GetRequiredService<ICorrelationContextAccessor>();

                return Problems.ToResult(
                    new Error(ErrorCodes.ValidationFailed, summary, ErrorCategory.Validation),
                    accessor.Current);
            }
        }

        return await next(context);
    }
}
