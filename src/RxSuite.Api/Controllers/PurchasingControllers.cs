using Microsoft.AspNetCore.Mvc;
using RxSuite.Api.Common;
using RxSuite.Application.Abstractions;
using RxSuite.Application.Features.Purchasing;

namespace RxSuite.Api.Controllers;

// BRD 8.5 "Purchasing and suppliers".

#region Suppliers

[Route("api/suppliers")]
public sealed class SuppliersController(IDispatcher dispatcher) : ApiControllerBase(dispatcher)
{
    [HttpGet]
    public Task<ActionResult<IReadOnlyList<SupplierDto>>> GetAll(CancellationToken ct) => Send(new GetSuppliersQuery(), ct);

    [HttpPost]
    public Task<ActionResult<int>> Create([FromBody] CreateSupplierCommand command, CancellationToken ct) => SendCreated(command, ct);
}

#endregion

#region Purchase orders

[Route("api/purchase-orders")]
public sealed class PurchaseOrdersController(IDispatcher dispatcher) : ApiControllerBase(dispatcher)
{
    [HttpGet("{purchaseOrderId:int}")]
    public Task<ActionResult<PurchaseOrderDto>> GetById(int purchaseOrderId, CancellationToken ct) =>
        Send(new GetPurchaseOrderQuery(purchaseOrderId), ct);

    /// <summary>Creates and submits an order. Above the approval limit it waits for approval (AC-PUR-1).</summary>
    [HttpPost]
    public Task<ActionResult<PurchaseOrderCreated>> Create([FromBody] CreatePurchaseOrderCommand command, CancellationToken ct) =>
        SendCreated(command, ct);

    [HttpPost("{purchaseOrderId:int}/approve")]
    public Task<IActionResult> Approve(int purchaseOrderId, CancellationToken ct) =>
        SendNoContent(new ApprovePurchaseOrderCommand(purchaseOrderId), ct);
}

#endregion

#region Goods receipts

[Route("api/goods-receipts")]
public sealed class GoodsReceiptsController(IDispatcher dispatcher) : ApiControllerBase(dispatcher)
{
    /// <summary>Receives goods with batch, expiry, bonus and cost; stock and supplier balance are updated (PUR-06).</summary>
    [HttpPost]
    public Task<ActionResult<GoodsReceiptCreated>> Receive([FromBody] ReceiveGoodsCommand command, CancellationToken ct) =>
        SendCreated(command, ct);
}

#endregion
