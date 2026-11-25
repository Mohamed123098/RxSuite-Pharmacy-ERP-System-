using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Domain.Common;
using RxSuite.Domain.Identity;
using RxSuite.Domain.Rules;
using RxSuite.Domain.Sales;
using RxSuite.Domain.Tax;

namespace RxSuite.Application.Features.Sales;

// USE CASES: read a receipt, and return items against it (POS-14, ACC-06, AC-ACC-2; Figure 12).
// Not built yet: returns without a receipt (manager PIN), exchanges, moving non-resaleable
// items into a quarantine stock record.

#region Query: one receipt

[RequiresPermission(Permissions.PosSell)]
public sealed record GetSaleQuery(long SaleId) : IQuery<SaleReceiptDto>;

internal sealed class GetSaleHandler(ISaleRepository sales, IEtaDocumentRepository etaDocuments, ICurrentUser currentUser)
    : IRequestHandler<GetSaleQuery, SaleReceiptDto>
{
    public async Task<Result<SaleReceiptDto>> HandleAsync(GetSaleQuery q, CancellationToken ct)
    {
        var sale = await sales.GetByIdAsync(q.SaleId, ct);
        if (sale is null) return Errors.NotFound("Sale", q.SaleId);
        if (!currentUser.CanAccessBranch(sale.BranchId)) return Errors.BranchNotAllowed;

        return sale.ToReceipt(await etaDocuments.GetBySaleIdAsync(sale.Id, ct));
    }
}

#endregion

#region Command: return items

public sealed record ReturnLineRequest(int LineNumber, int Quantity, bool IsResaleable);

[RequiresPermission(Permissions.PosReturn)]
public sealed record ReturnSaleCommand(long SaleId, IReadOnlyList<ReturnLineRequest> Lines, RefundMethod RefundMethod,
    string Reason) : ICommand<SaleReturnDto>;

public sealed record SaleReturnDto(long Id, long SaleId, decimal RefundTotal, RefundMethod RefundMethod, EtaDocumentType EtaDocumentType);

internal sealed class ReturnSaleHandler(
    ISaleRepository sales, IShiftRepository shifts, IStockRepository stock, IEtaDocumentRepository etaDocuments,
    IItemRepository items, IAuditTrail audit, ICurrentUser currentUser, IClock clock, BusinessRules rules)
    : IRequestHandler<ReturnSaleCommand, SaleReturnDto>
{
    public async Task<Result<SaleReturnDto>> HandleAsync(ReturnSaleCommand c, CancellationToken ct)
    {
        if (c.Lines is null || c.Lines.Count == 0 || !Enum.IsDefined(c.RefundMethod))
            return Error.Validation("Return.Invalid", "A return needs at least one line and a refund method.");

        // Figure 12 "Find original receipt".
        var sale = await sales.GetByIdAsync(c.SaleId, ct);
        if (sale is null) return Errors.NotFound("Sale", c.SaleId);
        if (!currentUser.CanAccessBranch(sale.BranchId)) return Errors.BranchNotAllowed;

        // Controlled medicines do not come back over the counter: the register would need a patient
        // return entry that the BRD does not define. A pharmacist records them with a stock adjustment.
        var returnedItemIds = sale.Lines.Where(l => c.Lines.Any(r => r.LineNumber == l.LineNumber)).Select(l => l.ItemId).Distinct();
        if ((await items.GetByIdsAsync(returnedItemIds, ct)).Any(i => i.IsControlled)) return Errors.ControlledReturn;

        // The refund leaves the drawer of the cashier's open shift.
        var shift = await shifts.GetOpenAsync(sale.BranchId, currentUser.UserId, ct);
        if (shift is null) return Errors.ShiftNotOpen;

        // The domain checks the return policy and the quantities, and works out refund and stock.
        var saleReturn = SaleReturn.Create(sale,
            c.Lines.Select(l => new ReturnRequest(l.LineNumber, l.Quantity, l.IsResaleable)).ToList(),
            c.RefundMethod, c.Reason, shift.Id, currentUser.UserId, clock.UtcNow, rules.Pos.ReturnPolicyDays);

        await sales.AddReturnAsync(saleReturn, ct);

        if (!await stock.ApplyMovementsAsync(sale.BranchId, saleReturn.StockMovements(), ct)) return Errors.StockChanged;

        // ACC-06 / AC-ACC-2: the return document references the original document.
        var original = await etaDocuments.GetBySaleIdAsync(sale.Id, ct);
        var document = EtaDocument.ForReturn(sale.TenantId, sale.BranchId, saleReturn.Id, original, clock.UtcNow);
        await etaDocuments.AddAsync(document, ct);

        await audit.RecordAsync("POS", "Return", saleReturn.Id, newValue: saleReturn.RefundTotal.ToString("0.00"),
            reason: saleReturn.Reason, branchId: sale.BranchId, ct: ct);

        return new SaleReturnDto(saleReturn.Id, sale.Id, saleReturn.RefundTotal, saleReturn.RefundMethod, document.Type);
    }
}

#endregion
