using Microsoft.AspNetCore.Mvc;
using RxSuite.Api.Common;
using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Application.Features.Sales;
using RxSuite.Application.Features.Tax;
using RxSuite.Domain.Sales;
using RxSuite.Domain.Tax;

namespace RxSuite.Api.Controllers;

#region Shifts (POS-16)

[Route("api/shifts")]
public sealed class ShiftsController(IDispatcher dispatcher) : ApiControllerBase(dispatcher)
{
    public sealed record CashMovementRequest(CashMovementType Type, decimal Amount, string Reason);

    public sealed record CloseShiftRequest(decimal CountedCash, string? Note, ManagerOverride? Approval);

    [HttpPost]
    public Task<ActionResult<int>> Open([FromBody] OpenShiftCommand command, CancellationToken ct) => SendCreated(command, ct);

    /// <summary>X-report while the shift is open, Z-report after it is closed.</summary>
    [HttpGet("{shiftId:int}")]
    public Task<ActionResult<ShiftSummaryDto>> GetSummary(int shiftId, CancellationToken ct) =>
        Send(new GetShiftSummaryQuery(shiftId), ct);

    /// <summary>Pay-in or pay-out, always with a reason.</summary>
    [HttpPost("{shiftId:int}/cash-movements")]
    public Task<IActionResult> RecordCashMovement(int shiftId, [FromBody] CashMovementRequest request, CancellationToken ct) =>
        SendNoContent(new RecordCashMovementCommand(shiftId, request.Type, request.Amount, request.Reason), ct);

    /// <summary>Closes the shift with the counted cash; a large variance needs a manager PIN.</summary>
    [HttpPost("{shiftId:int}/close")]
    public Task<ActionResult<ShiftSummaryDto>> Close(int shiftId, [FromBody] CloseShiftRequest request, CancellationToken ct) =>
        Send(new CloseShiftCommand(shiftId, request.CountedCash, request.Note, request.Approval), ct);
}

#endregion

#region Sales and returns (BRD 8.3)

[Route("api/sales")]
public sealed class SalesController(IDispatcher dispatcher) : ApiControllerBase(dispatcher)
{
    public sealed record ReturnRequestBody(IReadOnlyList<ReturnLineRequest> Lines, RefundMethod RefundMethod, string Reason);

    /// <summary>
    /// Completes a sale: FEFO batches, discounts within limits, pharmacist approval for Rx items,
    /// split payment, stock update and a queued ETA document. Send the terminal's OfflineId for
    /// sales made offline, so a repeated sync never creates a duplicate (BR-POS-5).
    /// </summary>
    [HttpPost]
    public Task<ActionResult<SaleReceiptDto>> Create([FromBody] CreateSaleCommand command, CancellationToken ct) =>
        SendCreated(command, ct);

    [HttpGet("{saleId:long}")]
    public Task<ActionResult<SaleReceiptDto>> GetById(long saleId, CancellationToken ct) => Send(new GetSaleQuery(saleId), ct);

    /// <summary>Returns items against the original receipt (POS-14); issues the ETA return document.</summary>
    [HttpPost("{saleId:long}/returns")]
    public Task<ActionResult<SaleReturnDto>> Return(long saleId, [FromBody] ReturnRequestBody request, CancellationToken ct) =>
        SendCreated(new ReturnSaleCommand(saleId, request.Lines, request.RefundMethod, request.Reason), ct);
}

#endregion

#region ETA documents (BRD 8.9)

[Route("api/eta-documents")]
public sealed class EtaDocumentsController(IDispatcher dispatcher) : ApiControllerBase(dispatcher)
{
    /// <summary>ACC-04/05: the documents monitor; filter by status to see the errors.</summary>
    [HttpGet]
    public Task<ActionResult<PagedResult<EtaDocumentDto>>> Get(
        [FromQuery] EtaDocumentStatus? status, CancellationToken ct,
        [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 50) =>
        Send(new GetEtaDocumentsQuery(status, pageNumber, pageSize), ct);

    /// <summary>AC-ACC-1: puts a rejected document back in the queue after its data was fixed.</summary>
    [HttpPost("{documentId:long}/resubmit")]
    public Task<IActionResult> Resubmit(long documentId, CancellationToken ct) =>
        SendNoContent(new ResubmitEtaDocumentCommand(documentId), ct);
}

#endregion
