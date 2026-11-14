using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RxSuite.Application.Abstractions;
using RxSuite.Domain.Common;

namespace RxSuite.Api.Common;

/// <summary>
/// Base class of every controller. Controllers hold no logic: they turn HTTP into a command or
/// query, send it through the dispatcher, and this class turns the result back into HTTP
/// (RFC 7807 "problem details" for errors). Every endpoint needs a login unless it says otherwise.
/// </summary>
[ApiController]
[Authorize]
[Produces("application/json")]
public abstract class ApiControllerBase(IDispatcher dispatcher) : ControllerBase
{
    protected IDispatcher Dispatcher { get; } = dispatcher;

    #region Sending requests

    /// <summary>200 with the value, or the matching error.</summary>
    protected async Task<ActionResult<T>> Send<T>(IRequest<T> request, CancellationToken ct)
    {
        var result = await Dispatcher.SendAsync(request, ct);
        return result.IsSuccess ? Ok(result.Value) : ToProblem(result.Error);
    }

    /// <summary>201 with the value (a new Id or the created resource), or the matching error.</summary>
    protected async Task<ActionResult<T>> SendCreated<T>(IRequest<T> request, CancellationToken ct)
    {
        var result = await Dispatcher.SendAsync(request, ct);
        return result.IsSuccess ? StatusCode(StatusCodes.Status201Created, result.Value) : ToProblem(result.Error);
    }

    /// <summary>204 for commands that return nothing, or the matching error.</summary>
    protected async Task<IActionResult> SendNoContent(IRequest<Unit> request, CancellationToken ct)
    {
        var result = await Dispatcher.SendAsync(request, ct);
        return result.IsSuccess ? NoContent() : ToProblem(result.Error);
    }

    #endregion

    #region Errors

    /// <summary>Maps the error category to an HTTP status code.</summary>
    protected ObjectResult ToProblem(Error error)
    {
        var statusCode = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError
        };

        return Problem(detail: error.Message, title: error.Code, statusCode: statusCode);
    }

    #endregion
}
