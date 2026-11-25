using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Domain.Common;
using RxSuite.Domain.Controlled;
using RxSuite.Domain.Identity;
using RxSuite.Domain.Inventory;
using RxSuite.Domain.Rules;

namespace RxSuite.Application.Features.Inventory;

// USE CASES: stock adjustments (INV-09, BR-INV-3), the stock card (INV-12), expiry and
// low-stock alerts (INV-06, INV-07, AC-INV-3; Figure 7), recalls (POS-05, AC-POS-4) and the
// nightly quarantine of expired batches (INV-08, AC-INV-1).

#region Read models

/// <summary>One line of an item's stock card (INV-12).</summary>
public sealed class StockCardLine
{
    public DateTime CreatedAtUtc { get; set; }
    public MovementType Type { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public long DocumentId { get; set; }
    public string? Reason { get; set; }
    public string? UserName { get; set; }
}

/// <summary>A batch on the near-expiry board (INV-07).</summary>
public sealed class ExpiryAlert
{
    public int BatchId { get; set; }
    public int ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public string BatchNumber { get; set; } = string.Empty;
    public DateOnly ExpiryDate { get; set; }
    public int Quantity { get; set; }
    public decimal StockValue { get; set; }
    public BatchStatus Status { get; set; }
}

/// <summary>An item at or below its reorder level (AC-INV-3).</summary>
public sealed class LowStockAlert
{
    public int ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int AvailableQuantity { get; set; }
    public int ReorderLevel { get; set; }
}

public sealed record StockAlertsDto(IReadOnlyList<ExpiryAlert> NearExpiry, IReadOnlyList<LowStockAlert> LowStock);

#endregion

#region Command: adjust stock

/// <summary>
/// Changes the quantity of a batch with a reason code. <c>QuantityChange</c> is signed and in
/// the smallest unit. A reduction worth more than the limit needs a manager (BR-INV-3).
/// </summary>
[RequiresPermission(Permissions.StockAdjust)]
public sealed record AdjustStockCommand(int BatchId, int QuantityChange, AdjustmentReason Reason, string? Note,
    ManagerOverride? Approval) : ICommand<Unit>;

internal sealed class AdjustStockHandler(
    IStockRepository stock, IItemRepository items, IControlledRegisterRepository register, IPharmacistCheck pharmacists,
    ICurrentUser currentUser, IOverrideAuthorizer overrides, IAuditTrail audit, IClock clock, BusinessRules rules)
    : IRequestHandler<AdjustStockCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(AdjustStockCommand c, CancellationToken ct)
    {
        if (c.QuantityChange == 0) return Error.Validation("Stock.NoChange", "The quantity change cannot be zero.");
        if (!Enum.IsDefined(c.Reason)) return Error.Validation("Stock.Reason", "The reason code is not valid.");

        var batch = await stock.GetBatchAsync(c.BatchId, ct);
        if (batch is null) return Errors.NotFound("Batch", c.BatchId);
        if (!currentUser.CanAccessBranch(batch.BranchId)) return Errors.BranchNotAllowed;

        // CTL-02 / CTL-04: a controlled item is adjusted by a pharmacist, and the register follows.
        var item = await items.GetByIdAsync(batch.ItemId, ct);
        int? pharmacistId = null;
        if (item is { IsControlled: true })
        {
            var pharmacist = await pharmacists.CurrentAsync(ct);
            if (pharmacist.IsFailure) return pharmacist.Error;
            pharmacistId = pharmacist.Value.Id;
        }

        // BR-INV-3: a large loss needs a second person unless the user may approve it themselves.
        string? approvedBy = null;
        var lostValue = c.QuantityChange < 0 ? -c.QuantityChange * batch.UnitCost : 0m;
        if (lostValue > rules.Inventory.AdjustmentApprovalLimit && !currentUser.Has(Permissions.StockAdjustApprove))
        {
            var approval = await overrides.AuthorizeAsync(c.Approval, Permissions.StockAdjustApprove, ct);
            if (approval.IsFailure) return approval.Error;
            approvedBy = approval.Value.FullName;
        }

        var movement = new StockMovement(batch.Id, c.QuantityChange, MovementType.Adjustment, "Adjustment", 0,
            $"{c.Reason}{(string.IsNullOrWhiteSpace(c.Note) ? "" : ": " + c.Note.Trim())}");

        if (!await stock.ApplyMovementsAsync(batch.BranchId, [movement], ct))
            return Error.Conflict("Stock.Insufficient", "The batch does not hold that much stock.");

        if (pharmacistId is { } by)
        {
            // CTL-04 "out (destruction)" for expired or damaged stock; any other change is an adjustment.
            var type = c.QuantityChange < 0 && c.Reason is AdjustmentReason.Expiry or AdjustmentReason.Damage
                ? RegisterEntryType.Destruction
                : RegisterEntryType.Adjustment;
            var entry = ControlledRegisterEntry.ForStockMovement(currentUser.TenantId, batch.BranchId, batch.ItemId,
                batch.Id, type, c.QuantityChange, "StockAdjustment", batch.Id, by, movement.Reason, clock.UtcNow);
            if (!await register.AddEntriesAsync([entry], ct)) return Errors.RegisterNegative;
        }

        await audit.RecordAsync("Inventory", "StockAdjusted", batch.Id, batch.Quantity.ToString(),
            (batch.Quantity + c.QuantityChange).ToString(),
            approvedBy is null ? movement.Reason : $"{movement.Reason} (approved by {approvedBy})", batch.BranchId, ct);
        return Unit.Value;
    }
}

#endregion

#region Command: recall a batch

/// <summary>An EDA recall: block the batch in every branch so it cannot be sold (POS-05, AC-POS-4).</summary>
[RequiresPermission(Permissions.StockAdjustApprove)]
public sealed record RecallBatchCommand(int ItemId, string BatchNumber, string Reason) : ICommand<int>;

internal sealed class RecallBatchHandler(IStockRepository stock, IAuditTrail audit) : IRequestHandler<RecallBatchCommand, int>
{
    public async Task<Result<int>> HandleAsync(RecallBatchCommand c, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.BatchNumber) || string.IsNullOrWhiteSpace(c.Reason))
            return Error.Validation("Recall.Incomplete", "The batch number and the reason are required.");

        var blocked = await stock.RecallBatchAsync(c.ItemId, c.BatchNumber.Trim(), ct);
        await audit.RecordAsync("Inventory", "BatchRecalled", c.ItemId, newValue: c.BatchNumber, reason: c.Reason, ct: ct);
        return blocked;
    }
}

#endregion

#region Command: nightly quarantine

/// <summary>Run by the nightly job: expired batches go to quarantine and can no longer be sold (INV-08).</summary>
[NoUserRequired]
public sealed record QuarantineExpiredBatchesCommand : ICommand<int>;

internal sealed class QuarantineExpiredBatchesHandler(IStockRepository stock, IClock clock)
    : IRequestHandler<QuarantineExpiredBatchesCommand, int>
{
    public async Task<Result<int>> HandleAsync(QuarantineExpiredBatchesCommand request, CancellationToken ct) =>
        await stock.QuarantineExpiredAsync(clock.Today, ct);
}

#endregion

#region Query: stock card

[RequiresPermission(Permissions.InventoryView)]
public sealed record GetStockCardQuery(int BranchId, int ItemId) : IQuery<IReadOnlyList<StockCardLine>>;

internal sealed class GetStockCardHandler(IStockRepository stock, ICurrentUser currentUser)
    : IRequestHandler<GetStockCardQuery, IReadOnlyList<StockCardLine>>
{
    public async Task<Result<IReadOnlyList<StockCardLine>>> HandleAsync(GetStockCardQuery q, CancellationToken ct) =>
        currentUser.CanAccessBranch(q.BranchId)
            ? Result<IReadOnlyList<StockCardLine>>.Success(await stock.GetStockCardAsync(q.BranchId, q.ItemId, ct))
            : Errors.BranchNotAllowed;
}

#endregion

#region Query: expiry and low-stock alerts

[RequiresPermission(Permissions.InventoryView)]
public sealed record GetStockAlertsQuery(int BranchId) : IQuery<StockAlertsDto>;

internal sealed class GetStockAlertsHandler(IStockRepository stock, ICurrentUser currentUser, IClock clock, BusinessRules rules)
    : IRequestHandler<GetStockAlertsQuery, StockAlertsDto>
{
    public async Task<Result<StockAlertsDto>> HandleAsync(GetStockAlertsQuery q, CancellationToken ct)
    {
        if (!currentUser.CanAccessBranch(q.BranchId)) return Errors.BranchNotAllowed;

        var until = clock.Today.AddDays(rules.Inventory.NearExpiryDays);

        return new StockAlertsDto(
            await stock.GetNearExpiryAsync(q.BranchId, until, ct),
            await stock.GetLowStockAsync(q.BranchId, clock.Today, ct));
    }
}

#endregion
