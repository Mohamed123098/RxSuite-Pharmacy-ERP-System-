using Microsoft.AspNetCore.Mvc;
using RxSuite.Api.Common;
using RxSuite.Application.Abstractions;
using RxSuite.Application.Features.Transfers;
using RxSuite.Domain.Transfers;

namespace RxSuite.Api.Controllers;

#region Transfers (BRD 8.8 MBR-03, MBR-04)

[Route("api/transfers")]
public sealed class TransfersController(IDispatcher dispatcher) : ApiControllerBase(dispatcher)
{
    public sealed record ReasonRequest(string Reason);

    public sealed record ReceiveRequest(IReadOnlyList<TransferReceiptLine> Lines, string? Note);

    /// <summary>A branch asks another location for stock (US-MBR-2).</summary>
    [HttpPost("requests")]
    public Task<ActionResult<int>> CreateRequest([FromBody] RequestTransferCommand command, CancellationToken ct) => SendCreated(command, ct);

    /// <summary>HQ or the warehouse sends stock without a request; approved at once (MBR-04).</summary>
    [HttpPost("direct")]
    public Task<ActionResult<int>> CreateDirect([FromBody] CreateDirectTransferCommand command, CancellationToken ct) =>
        SendCreated(command, ct);

    /// <summary>Requests, in transit, received with shortage...: filter by branch and status.</summary>
    [HttpGet]
    public Task<ActionResult<IReadOnlyList<TransferSummaryDto>>> Get([FromQuery] int? branchId, [FromQuery] TransferStatus? status,
        CancellationToken ct) =>
        Send(new GetTransfersQuery(branchId, status), ct);

    [HttpGet("{transferId:int}")]
    public Task<ActionResult<TransferDto>> GetById(int transferId, CancellationToken ct) => Send(new GetTransferQuery(transferId), ct);

    [HttpPost("{transferId:int}/approve")]
    public Task<IActionResult> Approve(int transferId, CancellationToken ct) => SendNoContent(new ApproveTransferCommand(transferId), ct);

    [HttpPost("{transferId:int}/reject")]
    public Task<IActionResult> Reject(int transferId, [FromBody] ReasonRequest request, CancellationToken ct) =>
        SendNoContent(new RejectTransferCommand(transferId, request.Reason), ct);

    [HttpPost("{transferId:int}/cancel")]
    public Task<IActionResult> Cancel(int transferId, [FromBody] ReasonRequest request, CancellationToken ct) =>
        SendNoContent(new CancelTransferCommand(transferId, request.Reason), ct);

    /// <summary>Picks FEFO batches at the source and ships them; the stock is now in transit (BR-MBR-1).</summary>
    [HttpPost("{transferId:int}/ship")]
    public Task<ActionResult<TransferDto>> Ship(int transferId, CancellationToken ct) => Send(new ShipTransferCommand(transferId), ct);

    /// <summary>Posts what arrived per shipped batch; anything missing is recorded as a shortage (AC-MBR-1).</summary>
    [HttpPost("{transferId:int}/receive")]
    public Task<ActionResult<TransferDto>> Receive(int transferId, [FromBody] ReceiveRequest request, CancellationToken ct) =>
        Send(new ReceiveTransferCommand(transferId, request.Lines, request.Note), ct);
}

#endregion

#region Branch settings and HQ views (MBR-02, MBR-05, MBR-06, MBR-08)

[Route("api/branches/{branchId:int}/items/{itemId:int}/settings")]
public sealed class BranchItemSettingsController(IDispatcher dispatcher) : ApiControllerBase(dispatcher)
{
    public sealed record SettingRequest(decimal? SellingPrice, int? MinStock, int? MaxStock, bool IsHidden);

    /// <summary>The branch's own price, min/max stock and visibility for one item; empty fields use the central item.</summary>
    [HttpPut]
    public Task<IActionResult> Set(int branchId, int itemId, [FromBody] SettingRequest request, CancellationToken ct) =>
        SendNoContent(new SetBranchItemSettingCommand(branchId, itemId, request.SellingPrice, request.MinStock, request.MaxStock,
            request.IsHidden), ct);
}

[Route("api/hq")]
public sealed class HeadquartersController(IDispatcher dispatcher) : ApiControllerBase(dispatcher)
{
    /// <summary>Stock of the matching items in every location, by batch and expiry, plus what is in transit.</summary>
    [HttpGet("stock-matrix")]
    public Task<ActionResult<IReadOnlyList<ItemStockDto>>> GetStockMatrix([FromQuery] string term, CancellationToken ct) =>
        Send(new GetStockMatrixQuery(term), ct);

    /// <summary>Near-expiry or overstocked batches that another branch would sell faster.</summary>
    [HttpGet("rebalancing")]
    public Task<ActionResult<IReadOnlyList<RebalancingSuggestionDto>>> GetRebalancing(CancellationToken ct,
        [FromQuery] int salesWindowDays = 30) =>
        Send(new GetRebalancingSuggestionsQuery(salesWindowDays), ct);

    /// <summary>Sales, margin, stock days and expiry losses per branch.</summary>
    [HttpGet("branch-performance")]
    public Task<ActionResult<IReadOnlyList<BranchPerformanceDto>>> GetBranchPerformance([FromQuery] DateOnly from,
        [FromQuery] DateOnly to, CancellationToken ct) =>
        Send(new GetBranchPerformanceQuery(from, to), ct);
}

#endregion
