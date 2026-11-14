using Microsoft.AspNetCore.Mvc;
using RxSuite.Api.Common;
using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Application.Features.Catalog;
using RxSuite.Application.Features.Inventory;
using RxSuite.Domain.Inventory;

namespace RxSuite.Api.Controllers;

#region Items (BRD 8.4 item master, 8.3 POS search)

[Route("api/items")]
public sealed class ItemsController(IDispatcher dispatcher) : ApiControllerBase(dispatcher)
{
    public sealed record ChangePriceRequest(decimal NewPrice, string? Reason);

    /// <summary>POS-01/02: search by barcode, code or name; returns price, stock and nearest expiry.</summary>
    [HttpGet("search")]
    public Task<ActionResult<IReadOnlyList<ItemSearchResult>>> Search(
        [FromQuery] int branchId, [FromQuery] string term, CancellationToken ct) =>
        Send(new SearchItemsQuery(branchId, term), ct);

    [HttpPost]
    public Task<ActionResult<int>> Create([FromBody] CreateItemCommand command, CancellationToken ct) => SendCreated(command, ct);

    /// <summary>Changes the selling price; the old and new price go to the audit trail (AC-USR-3).</summary>
    [HttpPut("{itemId:int}/price")]
    public Task<IActionResult> ChangePrice(int itemId, [FromBody] ChangePriceRequest request, CancellationToken ct) =>
        SendNoContent(new ChangeItemPriceCommand(itemId, request.NewPrice, request.Reason), ct);
}

#endregion

#region Stock (BRD 8.4)

[Route("api/stock")]
public sealed class StockController(IDispatcher dispatcher) : ApiControllerBase(dispatcher)
{
    public sealed record AdjustRequest(int QuantityChange, AdjustmentReason Reason, string? Note, ManagerOverride? Approval);

    public sealed record RecallRequest(int ItemId, string BatchNumber, string Reason);

    /// <summary>INV-12: the stock card of an item in a branch.</summary>
    [HttpGet("card")]
    public Task<ActionResult<IReadOnlyList<StockCardLine>>> GetCard(
        [FromQuery] int branchId, [FromQuery] int itemId, CancellationToken ct) =>
        Send(new GetStockCardQuery(branchId, itemId), ct);

    /// <summary>INV-07 / AC-INV-3: near-expiry batches and items at or below their reorder level.</summary>
    [HttpGet("alerts")]
    public Task<ActionResult<StockAlertsDto>> GetAlerts([FromQuery] int branchId, CancellationToken ct) =>
        Send(new GetStockAlertsQuery(branchId), ct);

    /// <summary>INV-09: adjusts a batch with a reason code; a large loss needs a manager PIN (BR-INV-3).</summary>
    [HttpPost("batches/{batchId:int}/adjust")]
    public Task<IActionResult> Adjust(int batchId, [FromBody] AdjustRequest request, CancellationToken ct) =>
        SendNoContent(new AdjustStockCommand(batchId, request.QuantityChange, request.Reason, request.Note, request.Approval), ct);

    /// <summary>POS-05: blocks a recalled batch in every branch. Returns how many stock records were blocked.</summary>
    [HttpPost("recalls")]
    public Task<ActionResult<int>> Recall([FromBody] RecallRequest request, CancellationToken ct) =>
        Send(new RecallBatchCommand(request.ItemId, request.BatchNumber, request.Reason), ct);
}

#endregion
