using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Domain.Common;
using RxSuite.Domain.Controlled;
using RxSuite.Domain.Identity;
using RxSuite.Domain.Purchasing;
using RxSuite.Domain.Rules;

namespace RxSuite.Application.Features.Purchasing;

// USE CASE (PUR-06, PUR-07, BR-PUR-1..3, AC-PUR-2, AC-PUR-3; Figure 6): receive goods against
// a purchase order or directly, with batch, expiry, bonus and cost for every line.
// Controlled items (CTL-02, CTL-04): only a pharmacist receives them, and each adds a register entry.

#region Command and result

public sealed record GoodsReceiptLineRequest(int ItemId, string BatchNumber, DateOnly ExpiryDate, int QuantityBoxes,
    int BonusBoxes, decimal CostPerBox, decimal DiscountPercent, string? ShelfLifeOverrideReason);

[RequiresPermission(Permissions.PurchasingReceive)]
public sealed record ReceiveGoodsCommand(int BranchId, int SupplierId, int? PurchaseOrderId,
    string? SupplierInvoiceNumber, IReadOnlyList<GoodsReceiptLineRequest> Lines) : ICommand<GoodsReceiptCreated>;

public sealed record GoodsReceiptCreated(int Id, decimal Total, PurchaseOrderStatus? PurchaseOrderStatus);

#endregion

#region Handler

internal sealed class ReceiveGoodsHandler(
    IGoodsReceiptRepository receipts, IPurchaseOrderRepository orders, ISupplierRepository suppliers,
    IItemRepository items, IStockRepository stock, IControlledRegisterRepository register, IPharmacistCheck pharmacists,
    ICurrentUser currentUser, IClock clock, BusinessRules rules)
    : IRequestHandler<ReceiveGoodsCommand, GoodsReceiptCreated>
{
    public async Task<Result<GoodsReceiptCreated>> HandleAsync(ReceiveGoodsCommand c, CancellationToken ct)
    {
        #region Checks

        if (!currentUser.CanAccessBranch(c.BranchId)) return Errors.BranchNotAllowed;
        if (c.Lines is null || c.Lines.Count == 0)
            return Error.Validation("GoodsReceipt.Empty", "A goods receipt needs at least one line.");

        var supplier = await suppliers.GetByIdAsync(c.SupplierId, ct);
        if (supplier is null) return Errors.NotFound("Supplier", c.SupplierId);

        var itemsById = (await items.GetByIdsAsync(c.Lines.Select(l => l.ItemId).Distinct(), ct)).ToDictionary(i => i.Id);
        if (c.Lines.FirstOrDefault(l => !itemsById.ContainsKey(l.ItemId)) is { } unknown)
            return Errors.NotFound("Item", unknown.ItemId);

        // CTL-02: controlled items are received by a pharmacist only.
        int? pharmacistId = null;
        if (c.Lines.Any(l => itemsById[l.ItemId].IsControlled))
        {
            var pharmacist = await pharmacists.CurrentAsync(ct);
            if (pharmacist.IsFailure) return pharmacist.Error;
            pharmacistId = pharmacist.Value.Id;
        }

        PurchaseOrder? order = null;
        if (c.PurchaseOrderId is { } orderId)
        {
            order = await orders.GetByIdAsync(orderId, ct);
            if (order is null) return Errors.NotFound("PurchaseOrder", orderId);
            if (order.SupplierId != c.SupplierId || order.BranchId != c.BranchId)
                return Error.Conflict("GoodsReceipt.OrderMismatch", "The purchase order is for another supplier or branch.");
        }

        #endregion

        #region Build the receipt (domain rules: shelf life, cost per box)

        var receipt = GoodsReceipt.Create(currentUser.TenantId, c.SupplierId, c.BranchId, c.PurchaseOrderId,
            c.SupplierInvoiceNumber, currentUser.UserId,
            c.Lines.Select(l => GoodsReceiptLine.Create(l.ItemId, l.BatchNumber, l.ExpiryDate, l.QuantityBoxes,
                l.BonusBoxes, l.CostPerBox, l.DiscountPercent, itemsById[l.ItemId].VatRate,
                l.ShelfLifeOverrideReason, clock.Today, rules.Purchasing.MinimumShelfLifeMonths)));

        #endregion

        #region Save: receipt, stock, order progress, supplier balance (one transaction)

        var receiptId = await receipts.AddAsync(receipt, ct);
        var registerEntries = new List<ControlledRegisterEntry>();

        foreach (var line in receipt.Lines)
        {
            var item = itemsById[line.ItemId];
            var quantity = line.TotalBoxes * item.UnitsPerBox;

            // BR-INV-1: stock and cost are kept per smallest unit. BR-PUR-1: bonus lowers the unit cost.
            var batchId = await stock.ReceiveBatchAsync(c.BranchId, item.Id, line.BatchNumber, line.ExpiryDate,
                quantity, unitCost: line.EffectiveCostPerBox / item.UnitsPerBox, receiptId, ct);

            order?.RegisterReceipt(item.Id, line.TotalBoxes);   // BR-PUR-3

            if (item.IsControlled)   // CTL-04: "in (purchase)"
                registerEntries.Add(ControlledRegisterEntry.ForStockMovement(currentUser.TenantId, c.BranchId, item.Id,
                    batchId, RegisterEntryType.Purchase, quantity, "GoodsReceipt", receiptId, pharmacistId!.Value,
                    c.SupplierInvoiceNumber is null ? null : $"Supplier invoice {c.SupplierInvoiceNumber}", clock.UtcNow));
        }

        if (registerEntries.Count > 0 && !await register.AddEntriesAsync(registerEntries, ct)) return Errors.RegisterNegative;
        if (order is not null) await orders.UpdateAsync(order, ct);
        await suppliers.IncreaseBalanceAsync(c.SupplierId, receipt.Total, ct);

        #endregion

        return new GoodsReceiptCreated(receiptId, receipt.Total, order?.Status);
    }
}

#endregion
