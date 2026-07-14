using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using TodoApp.Application.Common.Exceptions;

namespace TodoApp.WebApi;

/// <summary>
/// Translates application exceptions into RFC 7807 problem responses so the
/// API returns consistent, structured errors.
/// </summary>
public class GlobalExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetailsService;
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(
        IProblemDetailsService problemDetailsService,
        ILogger<GlobalExceptionHandler> logger)
    {
        _problemDetailsService = problemDetailsService;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ProblemDetails problemDetails;

        switch (exception)
        {
            case ValidationException validationException:
                problemDetails = new ValidationProblemDetails(
                    validationException.Errors.ToDictionary(kvp => kvp.Key, kvp => kvp.Value))
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "One or more validation errors occurred.",
                    Type = "https://tools.ietf.org/html/rfc7231#section-6.5.1"
                };
                break;

            case NotFoundException notFoundException:
                problemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status404NotFound,
                    Title = "Resource not found.",
                    Detail = notFoundException.Message,
                    Type = "https://tools.ietf.org/html/rfc7231#section-6.5.4"
                };
                break;

            case UnauthorizedException unauthorizedException:
                problemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status401Unauthorized,
                    Title = "Authentication failed.",
                    Detail = unauthorizedException.Message,
                    Type = "https://tools.ietf.org/html/rfc7235#section-3.1"
                };
                break;

            case ForbiddenAccessException forbiddenException:
                problemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status403Forbidden,
                    Title = "Access denied.",
                    Detail = forbiddenException.Message,
                    Type = "https://tools.ietf.org/html/rfc7231#section-6.5.3"
                };
                break;

            case ConflictException conflictException:
                problemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "Request conflict.",
                    Detail = conflictException.Message,
                    Type = "https://tools.ietf.org/html/rfc7231#section-6.5.8"
                };
                break;

            case ConcurrencyConflictException concurrencyException:
                problemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "The resource was modified by someone else.",
                    Detail = concurrencyException.Message,
                    Type = "https://tools.ietf.org/html/rfc7231#section-6.5.8"
                };
                // Hand the client the latest server state so it can reload and re-apply.
                if (concurrencyException.CurrentValue is not null)
                {
                    problemDetails.Extensions["current"] = concurrencyException.CurrentValue;
                }
                break;

            default:
                _logger.LogError(exception, "Unhandled exception processing {Path}", httpContext.Request.Path);
                problemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "An unexpected error occurred.",
                    Type = "https://tools.ietf.org/html/rfc7231#section-6.6.1"
                };
                break;
        }

        httpContext.Response.StatusCode = problemDetails.Status ?? StatusCodes.Status500InternalServerError;

        return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problemDetails
        });
    }
}
