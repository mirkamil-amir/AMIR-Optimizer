using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Research;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Api.Infrastructure;

/// <summary>Maps application exceptions to RFC 9457 Problem Details responses.</summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails problem = exception switch
        {
            ValidationException v => new ValidationProblemDetails(v.Errors)
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Validation failed",
                Detail = v.Errors.Count == 0 ? v.Message : null,
            },
            NotFoundException => new ProblemDetails { Status = StatusCodes.Status404NotFound, Title = "Not found", Detail = exception.Message },
            AuthenticationFailedException => new ProblemDetails { Status = StatusCodes.Status401Unauthorized, Title = "Sign-in failed", Detail = exception.Message },
            ForbiddenException => new ProblemDetails { Status = StatusCodes.Status403Forbidden, Title = "Forbidden", Detail = exception.Message },
            ConflictException => new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "Conflict", Detail = exception.Message },
            ToolException t => new ProblemDetails
            {
                Status = t.Code switch
                {
                    "invalid_arguments" => StatusCodes.Status400BadRequest,
                    "unauthorized" => StatusCodes.Status404NotFound,
                    "confirmation_required" or "budget_exceeded" => StatusCodes.Status409Conflict,
                    "timeout" => StatusCodes.Status504GatewayTimeout,
                    _ => StatusCodes.Status502BadGateway,
                },
                Title = t.Code switch
                {
                    "confirmation_required" => "Confirmation required",
                    "budget_exceeded" => "Session budget used up",
                    "timeout" => "Analysis timed out",
                    "invalid_arguments" => "Invalid analysis request",
                    "unauthorized" => "Not found",
                    _ => "Analysis failed",
                },
                Detail = t.Message,
                Extensions = { ["code"] = t.Code },
            },
            DomainException => new ProblemDetails { Status = StatusCodes.Status422UnprocessableEntity, Title = "Business rule violated", Detail = exception.Message },
            MarketDataProviderException => new ProblemDetails { Status = StatusCodes.Status502BadGateway, Title = "Market data provider error", Detail = exception.Message },
            // A stored model failed its checksum/format check or its file is missing: never load or substitute it.
            InvalidDataException or FileNotFoundException => new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Model artifact unavailable",
                Detail = $"The stored model could not be loaded safely: {exception.Message}",
            },
            OperationCanceledException when context.RequestAborted.IsCancellationRequested =>
                new ProblemDetails { Status = 499, Title = "Client closed request" },
            _ => new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Unexpected error",
                Detail = "An unexpected error occurred. The error has been logged.",
            },
        };

        if (problem.Status >= 500) logger.LogError(exception, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
        else logger.LogInformation("Request {Method} {Path} failed with {Status}: {Message}", context.Request.Method, context.Request.Path, problem.Status, exception.Message);

        context.Response.StatusCode = problem.Status ?? 500;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}
