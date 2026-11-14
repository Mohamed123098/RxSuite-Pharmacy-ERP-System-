using Microsoft.AspNetCore.Mvc;
using RxSuite.Api.Common;
using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Application.Features.Identity;

namespace RxSuite.Api.Controllers;

#region Users (BRD 8.2)

[Route("api/users")]
public sealed class UsersController(IDispatcher dispatcher) : ApiControllerBase(dispatcher)
{
    [HttpGet]
    public Task<ActionResult<IReadOnlyList<UserDto>>> GetAll(CancellationToken ct) => Send(new GetUsersQuery(), ct);

    /// <summary>Creates a user with a role and branches, within the plan's user limit.</summary>
    [HttpPost]
    public Task<ActionResult<int>> Create([FromBody] CreateUserCommand command, CancellationToken ct) => SendCreated(command, ct);

    /// <summary>US-USR-5: blocks a user at once. The user is kept for history (BR-USR-3).</summary>
    [HttpPost("{userId:int}/disable")]
    public Task<IActionResult> Disable(int userId, CancellationToken ct) => SendNoContent(new DisableUserCommand(userId), ct);
}

#endregion

#region Audit log (USR-12)

[Route("api/audit-log")]
public sealed class AuditLogController(IDispatcher dispatcher) : ApiControllerBase(dispatcher)
{
    /// <summary>Read-only viewer with filters: user, branch, module and date range.</summary>
    [HttpGet]
    public Task<ActionResult<PagedResult<AuditEntryDto>>> Get(
        [FromQuery] int? userId, [FromQuery] int? branchId, [FromQuery] string? module,
        [FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc,
        CancellationToken ct, [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 50) =>
        Send(new GetAuditLogQuery(pageNumber, pageSize, userId, branchId, module, fromUtc, toUtc), ct);
}

#endregion
