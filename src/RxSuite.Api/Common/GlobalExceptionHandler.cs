using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using RxSuite.Domain.Common;

namespace RxSuite.Api.Common;

/// <summary>
/// The last line of defence for exceptions nobody handled.
/// - <see cref="DomainException"/>: a broken business rule, returned as 400 with its message.
/// - anything else: logged in full, returned as 500 WITHOUT details (never leak internals).
/// </summary>
internal sealed class GlobalExceptionHandler(IProblemDetailsService problemDetails, ILogger<GlobalExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var problem = exception is DomainException ? BusinessRuleProblem(exception) : UnexpectedProblem(exception);

        httpContext.Response.StatusCode = problem.Status!.Value;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception
        });
    }

    #region Problem builders

    private static ProblemDetails BusinessRuleProblem(Exception exception) => new()
    {
        Status = StatusCodes.Status400BadRequest,
        Title = "Domain.RuleViolation",
        Detail = exception.Message
    };

    private ProblemDetails UnexpectedProblem(Exception exception)
    {
        logger.LogError(exception, "Unhandled exception");

        return new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "Server.Error",
            Detail = "An unexpected error occurred. Please try again or contact support."
        };
    }

    #endregion
}
