using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Domain.Catalog;
using RxSuite.Domain.Clinical;
using RxSuite.Domain.Controlled;
using RxSuite.Domain.Common;
using RxSuite.Domain.Identity;
using RxSuite.Domain.Inventory;
using RxSuite.Domain.Rules;
using RxSuite.Domain.Sales;
using RxSuite.Domain.Tax;

namespace RxSuite.Application.Features.Sales;

// USE CASE (Figure 2 "POS sale flow"): complete a sale at the counter.
// Covers POS-03/04/05/07/10/13, BR-POS-1/3/4/5/6, AC-POS-2/3/4, AC-USR-1, BR-USR-4, and the
// dispensing rules of BRD 8.6/8.7 through the DispensingPlanner: BR-RX-1/2, RX-02/03/06/10,
// CTL-02/03/04/08, AC-RX-2, AC-CTL-1.
// Not built yet: customer, insurance and loyalty on the sale (POS-09), price override (POS-08),
// selling with negative stock by manager PIN (POS-06: this build always blocks).

#region DTOs

public sealed record SaleLineDto(int LineNumber, int ItemId, string ItemName, UnitType UnitType, int Quantity,
    decimal UnitPrice, decimal DiscountPercent, decimal VatAmount, decimal LineTotal, int ReturnedQuantity);

public sealed record SalePaymentDto(PaymentMethod Method, decimal Amount, string? Reference);

/// <summary>The receipt (POS-12). The ETA UUID and QR arrive once the tax document is valid.</summary>
public sealed record SaleReceiptDto(long Id, int BranchId, int ShiftId, int CashierId, DateTime SoldAtUtc,
    decimal InvoiceDiscountPercent, decimal Total, decimal VatTotal, decimal ChangeGiven,
    IReadOnlyList<SaleLineDto> Lines, IReadOnlyList<SalePaymentDto> Payments,
    EtaDocumentStatus? EtaStatus, string? EtaUuid, string? EtaQrCode);

internal static class SaleMappings
{
    public static SaleReceiptDto ToReceipt(this Sale sale, EtaDocument? document) => new(
        sale.Id, sale.BranchId, sale.ShiftId, sale.CashierId, sale.SoldAtUtc, sale.InvoiceDiscountPercent,
        sale.Total, sale.VatTotal, sale.ChangeGiven,
        sale.Lines.Select(l => new SaleLineDto(l.LineNumber, l.ItemId, l.ItemName, l.UnitType, l.Quantity,
            l.UnitPrice, l.DiscountPercent, l.VatAmount, l.LineTotal, l.ReturnedQuantity)).ToList(),
        sale.Payments.Select(p => new SalePaymentDto(p.Method, p.Amount, p.Reference)).ToList(),
        document?.Status, document?.Uuid, document?.QrCode);
}

#endregion

#region Command

/// <param name="PrescriptionLineNumber">The prescription line this sale line serves (RX-06). Linked automatically
/// when exactly one open line of the prescription is for the same item.</param>
public sealed record SaleLineRequest(int ItemId, UnitType Unit, int Quantity, decimal DiscountPercent = 0, int? BatchId = null,
    int? PrescriptionLineNumber = null);

public sealed record PaymentRequest(PaymentMethod Method, decimal Amount, string? Reference = null);

public sealed record BuyerRequest(string? Name, string? NationalId, string? TaxNumber);

/// <param name="OfflineId">BR-POS-5: the terminal's unique ID for a sale made offline.</param>
/// <param name="DiscountApproval">Manager PIN when the discount is above the cashier's limit.</param>
/// <param name="PharmacistApproval">Pharmacist PIN when the cashier is not a licensed pharmacist (not enough for controlled items).</param>
/// <param name="PrescriptionId">The recorded prescription being dispensed (BR-RX-1, RX-06).</param>
/// <param name="AlertOverrides">BR-RX-2: the pharmacist's reason for each major or contraindicated alert.</param>
/// <param name="CounsellingNote">RX-10: optional counselling notes for this dispense.</param>
[RequiresPermission(Permissions.PosSell)]
public sealed record CreateSaleCommand(int BranchId, IReadOnlyList<SaleLineRequest> Lines,
    IReadOnlyList<PaymentRequest> Payments, decimal InvoiceDiscountPercent = 0, BuyerRequest? Buyer = null,
    Guid? OfflineId = null, ManagerOverride? DiscountApproval = null, ManagerOverride? PharmacistApproval = null,
    int? PrescriptionId = null, IReadOnlyList<AlertOverride>? AlertOverrides = null, string? CounsellingNote = null)
    : ICommand<SaleReceiptDto>;

internal sealed class CreateSaleValidator : IValidator<CreateSaleCommand>
{
    public IReadOnlyList<string> Validate(CreateSaleCommand c)
    {
        var problems = new List<string>();

        if (c.Lines is null || c.Lines.Count == 0) problems.Add("A sale needs at least one item.");
        else if (c.Lines.Any(l => l.Quantity <= 0 || !Enum.IsDefined(l.Unit))) problems.Add("Every line needs a unit and a quantity above zero.");
        if (c.Payments is null || c.Payments.Count == 0) problems.Add("A sale needs at least one payment.");
        else if (c.Payments.Any(p => !Enum.IsDefined(p.Method))) problems.Add("A payment method is not valid.");
        if (c.InvoiceDiscountPercent is < 0 or > 100) problems.Add("Discount must be between 0 and 100.");

        return problems;
    }
}

#endregion

#region Handler

internal sealed class CreateSaleHandler(
    ISaleRepository sales, IShiftRepository shifts, IItemRepository items, IStockRepository stock,
    IEtaDocumentRepository etaDocuments, IUserRepository users, IOverrideAuthorizer overrides,
    IDispensingPlanner dispensing, IPrescriptionRepository prescriptions, IDispenseRepository dispenses,
    IControlledRegisterRepository register, IAuditTrail audit, ICurrentUser currentUser, IClock clock, BusinessRules rules)
    : IRequestHandler<CreateSaleCommand, SaleReceiptDto>
{
    public async Task<Result<SaleReceiptDto>> HandleAsync(CreateSaleCommand c, CancellationToken ct)
    {
        #region 1. Branch, idempotency, open shift

        if (!currentUser.CanAccessBranch(c.BranchId)) return Errors.BranchNotAllowed;

        // BR-POS-5: an offline sale that was already synced is returned, not saved again.
        if (c.OfflineId is { } offlineId && await sales.GetIdByOfflineIdAsync(offlineId, ct) is { } existingId)
        {
            var existing = await sales.GetByIdAsync(existingId, ct);
            return existing!.ToReceipt(await etaDocuments.GetBySaleIdAsync(existingId, ct));
        }

        var shift = await shifts.GetOpenAsync(c.BranchId, currentUser.UserId, ct);
        if (shift is null) return Errors.ShiftNotOpen;

        #endregion

        #region 2. Items and FEFO batch allocation

        var itemIds = c.Lines.Select(l => l.ItemId).Distinct().ToList();
        var itemsById = (await items.GetByIdsAsync(itemIds, ct)).Where(i => i.IsActive).ToDictionary(i => i.Id);
        if (c.Lines.FirstOrDefault(l => !itemsById.ContainsKey(l.ItemId)) is { } unknown)
            return Errors.NotFound("Item", unknown.ItemId);

        var batches = await stock.GetBatchesAsync(c.BranchId, itemIds, ct);
        var lines = new List<SaleLine>();

        foreach (var (request, index) in c.Lines.Select((l, i) => (l, i)))
        {
            var item = itemsById[request.ItemId];

            var allocation = FefoAllocator.Allocate(batches.Where(b => b.ItemId == item.Id),
                item.ToBaseUnits(request.Quantity, request.Unit), clock.Today, request.BatchId);
            if (allocation.IsFailure) return allocation.Error;

            // Two lines of the same item must not use the same units twice.
            foreach (var part in allocation.Value) batches.First(b => b.Id == part.BatchId).Take(part.Quantity);

            lines.Add(SaleLine.Create(index + 1, item, request.Unit, request.Quantity,
                item.PriceFor(request.Unit, rules.Pos.UnitPriceRounding), request.DiscountPercent,
                c.InvoiceDiscountPercent, allocation.Value));
        }

        #endregion

        #region 3. Discount limit (POS-07, AC-USR-1)

        // Line and invoice discounts combine; the largest combined rate is checked against the role limit.
        var highestDiscount = c.Lines.Max(l => 100m * (1 - (1 - l.DiscountPercent / 100m) * (1 - c.InvoiceDiscountPercent / 100m)));
        User? discountApprover = null;

        if (highestDiscount > currentUser.MaxDiscountPercent)
        {
            var approval = await overrides.AuthorizeAsync(c.DiscountApproval, Permissions.PosOverride, ct);
            if (approval.IsFailure) return approval.Error;
            discountApprover = approval.Value;
        }

        #endregion

        #region 4. Pharmacist for Rx-only and controlled items (BR-USR-4), then the dispensing rules

        int? pharmacistId = null;
        var sellerIsPharmacist = false;
        if (c.PrescriptionId is not null || c.Lines.Any(l => itemsById[l.ItemId].RequiresPharmacist))
        {
            var cashier = await users.GetByIdAsync(currentUser.UserId, ct);
            sellerIsPharmacist = cashier is not null && cashier.CanActAsPharmacist(clock.Today) && currentUser.Has(Permissions.RxDispense);

            if (sellerIsPharmacist)
                pharmacistId = cashier!.Id;
            else
            {
                var approval = await overrides.AuthorizePharmacistAsync(c.PharmacistApproval, ct);
                if (approval.IsFailure) return approval.Error;
                pharmacistId = approval.Value.Id;
            }
        }

        // BRD 8.6 / 8.7: prescription link, clinical alerts and overrides, controlled rules.
        var plan = await dispensing.PlanAsync(c, itemsById, pharmacistId, sellerIsPharmacist, ct);
        if (plan.IsFailure) return plan.Error;

        #endregion

        #region 5. Build the receipt (totals, VAT, payments, change, buyer rule)

        var buyer = c.Buyer is null ? null : new Buyer(c.Buyer.Name, c.Buyer.NationalId, c.Buyer.TaxNumber);

        var sale = Sale.Create(currentUser.TenantId, c.BranchId, shift.Id, currentUser.UserId, pharmacistId,
            c.OfflineId, buyer, c.InvoiceDiscountPercent, lines,
            c.Payments.Select(p => new TenderedPayment(p.Method, p.Amount, p.Reference)),
            clock.UtcNow, rules.Pos.BuyerIdentificationThreshold);

        #endregion

        #region 6. Save: receipt, stock, tax document, audit (one transaction)

        await sales.AddAsync(sale, ct);

        // The stored procedure refuses to take a batch below zero; then nothing is saved.
        if (!await stock.ApplyMovementsAsync(c.BranchId, sale.StockMovements(), ct)) return Errors.StockChanged;

        // POS-13 / ACC-03: every sale gets an ETA document, queued for submission.
        var document = EtaDocument.ForSale(sale.TenantId, sale.BranchId, sale.Id, sale.IsBusinessSale, clock.UtcNow);
        await etaDocuments.AddAsync(document, ct);

        if (discountApprover is not null)
            await audit.RecordAsync("POS", "DiscountOverride", sale.Id, newValue: $"{highestDiscount:0.##}%",
                reason: $"Cashier {currentUser.UserName}, approved by {discountApprover.FullName}", branchId: c.BranchId, ct: ct);

        #endregion

        #region 7. Save the dispense: prescription progress, dispense record, controlled register

        var result = await SaveDispensingAsync(plan.Value, sale, itemsById, pharmacistId, ct);
        if (result.IsFailure) return result.Error;

        #endregion

        return sale.ToReceipt(document);
    }

    /// <summary>RX-06, RX-10, BR-RX-2 and CTL-04 / AC-CTL-1, in the sale's transaction.</summary>
    private async Task<Result<Unit>> SaveDispensingAsync(DispensingPlan plan, Sale sale, IReadOnlyDictionary<int, Item> itemsById,
        int? pharmacistId, CancellationToken ct)
    {
        if (plan.Prescription is { } prescription) await prescriptions.UpdateAsync(prescription, ct);

        if (plan.Dispense is { } dispense)
        {
            dispense.AttachSale(sale.Id);
            await dispenses.AddAsync(dispense, ct);

            if (dispense.Overrides.Count > 0)
                await audit.RecordAsync("Clinical", "AlertOverridden", sale.Id,
                    newValue: string.Join(", ", dispense.Overrides.Select(o => o.AlertCode)),
                    reason: string.Join(" | ", dispense.Overrides.Select(o => o.Reason)), branchId: sale.BranchId, ct: ct);

            if (dispense.PrescriptionPending)
                await audit.RecordAsync("Clinical", "PrescriptionPending", sale.Id,
                    reason: "Rx-only item sold before the prescription was recorded (BR-RX-1)", branchId: sale.BranchId, ct: ct);
        }

        // AC-CTL-1: one register entry per batch the controlled line was taken from.
        var entries = sale.Lines
            .Where(l => itemsById[l.ItemId].IsControlled)
            .SelectMany(l => l.Allocations.Select(a => ControlledRegisterEntry.ForDispense(sale.TenantId, sale.BranchId,
                l.ItemId, a.BatchId, a.Quantity, sale.Id, plan.Party!, pharmacistId!.Value, sale.SoldAtUtc)))
            .ToList();

        if (entries.Count > 0 && !await register.AddEntriesAsync(entries, ct)) return Errors.RegisterNegative;
        return Unit.Value;
    }
}

#endregion
