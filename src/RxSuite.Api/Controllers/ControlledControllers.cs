using Microsoft.AspNetCore.Mvc;
using RxSuite.Api.Common;
using RxSuite.Application.Abstractions;
using RxSuite.Application.Features.Controlled;

namespace RxSuite.Api.Controllers;

#region Controlled drugs register (BRD 8.7)

[Route("api/controlled-register")]
public sealed class ControlledRegisterController(IDispatcher dispatcher) : ApiControllerBase(dispatcher)
{
    public sealed record CorrectionRequest(int QuantityChange, string Reason);

    /// <summary>The register of one item between two dates (Cairo calendar days), with the running balance.</summary>
    [HttpGet]
    public Task<ActionResult<IReadOnlyList<RegisterLineDto>>> Get([FromQuery] int branchId, [FromQuery] int itemId,
        [FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct) =>
        Send(new GetControlledRegisterQuery(branchId, itemId, from, to), ct);

    /// <summary>The same register as a CSV file for the inspector (CTL-07). The download is audited.</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] int branchId, [FromQuery] int itemId,
        [FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct)
    {
        var result = await Dispatcher.SendAsync(new ExportControlledRegisterQuery(branchId, itemId, from, to), ct);
        if (result.IsFailure) return ToProblem(result.Error);

        Response.Headers.CacheControl = "no-store";
        return File(result.Value.Content, result.Value.ContentType, result.Value.FileName);
    }

    /// <summary>CTL-07: opening, in, out and closing per controlled item for one month.</summary>
    [HttpGet("monthly-summary")]
    public Task<ActionResult<IReadOnlyList<RegisterMonthLineDto>>> GetMonthlySummary([FromQuery] int branchId,
        [FromQuery] int year, [FromQuery] int month, CancellationToken ct) =>
        Send(new GetControlledMonthlySummaryQuery(branchId, year, month), ct);

    /// <summary>US-CTL-3: register against stock and the last count, per controlled item. Use onlyNeedingAttention for the alerts.</summary>
    [HttpGet("status")]
    public Task<ActionResult<IReadOnlyList<ControlledItemStatusDto>>> GetStatus([FromQuery] int branchId,
        [FromQuery] bool onlyNeedingAttention, CancellationToken ct) =>
        Send(new GetControlledStatusQuery(branchId, onlyNeedingAttention), ct);

    /// <summary>CTL-06: the pharmacist's signed count of the locked cabinet.</summary>
    [HttpPost("balance-checks")]
    public Task<ActionResult<BalanceCheckResult>> RecordBalanceCheck([FromBody] RecordBalanceCheckCommand command, CancellationToken ct) =>
        SendCreated(command, ct);

    /// <summary>CTL-05: adds a correction that points to the wrong entry; the wrong entry is kept.</summary>
    [HttpPost("{entryId:long}/corrections")]
    public Task<IActionResult> Correct(long entryId, [FromBody] CorrectionRequest request, CancellationToken ct) =>
        SendNoContent(new CorrectRegisterEntryCommand(entryId, request.QuantityChange, request.Reason), ct);
}

#endregion
