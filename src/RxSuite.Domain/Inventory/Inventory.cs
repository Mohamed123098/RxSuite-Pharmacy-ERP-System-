using RxSuite.Domain.Common;

namespace RxSuite.Domain.Inventory;

// BRD 8.4 "Inventory and expiry management".

#region Enums

/// <summary>BRD key data "Batch": available / quarantine / recalled.</summary>
public enum BatchStatus : byte { Available = 1, Quarantine = 2, Recalled = 3 }

/// <summary>INV-12: the kinds of entry on a stock card.</summary>
public enum MovementType : byte { Purchase = 1, Sale = 2, SaleReturn = 3, Adjustment = 4, TransferOut = 5, TransferIn = 6 }

/// <summary>INV-09: reason codes for a stock adjustment.</summary>
public enum AdjustmentReason : byte { Damage = 1, Theft = 2, Expiry = 3, Sample = 4, InternalUse = 5, Correction = 6 }

#endregion

#region Batch

/// <summary>
/// Stock of one item in one branch from one batch. BR-INV-2: a batch is identified by
/// item + batch number + expiry date. Quantity is in the smallest unit (BR-INV-1).
/// </summary>
public sealed class Batch : TenantEntity<int>
{
    public int BranchId { get; private set; }
    public int ItemId { get; private set; }
    public string BatchNumber { get; private set; } = string.Empty;
    public DateOnly ExpiryDate { get; private set; }
    public int Quantity { get; private set; }

    /// <summary>Cost of one smallest unit.</summary>
    public decimal UnitCost { get; private set; }
    public BatchStatus Status { get; private set; }

    private Batch() { }

    public static Batch Create(int id, int tenantId, int branchId, int itemId, string batchNumber,
        DateOnly expiryDate, int quantity, decimal unitCost, BatchStatus status = BatchStatus.Available) =>
        new()
        {
            Id = id, TenantId = tenantId, BranchId = branchId, ItemId = itemId, BatchNumber = batchNumber,
            ExpiryDate = expiryDate, Quantity = quantity, UnitCost = unitCost, Status = status
        };

    /// <summary>Takes stock out of the in-memory picture, so two lines of one sale cannot use the same units.</summary>
    public void Take(int quantity)
    {
        if (quantity <= 0 || quantity > Quantity) throw new DomainException("Cannot take that quantity from the batch.");
        Quantity -= quantity;
    }

    /// <summary>POS-05: expired, quarantined and recalled batches cannot be sold.</summary>
    public bool IsSellable(DateOnly today) =>
        Status == BatchStatus.Available && ExpiryDate >= today && Quantity > 0;
}

#endregion

#region FEFO allocation (domain service)

/// <summary>Part of a requested quantity taken from one batch.</summary>
public sealed record BatchAllocation(int BatchId, int Quantity, decimal UnitCost);

/// <summary>
/// POS-04 / AC-POS-2: picks the batches a sale takes stock from, First-Expiry-First-Out.
/// A pure function over the batches it is given, so it is easy to test.
/// </summary>
public static class FefoAllocator
{
    public static Result<IReadOnlyList<BatchAllocation>> Allocate(
        IEnumerable<Batch> batchesOfItem, int quantity, DateOnly today, int? preferredBatchId = null)
    {
        if (quantity <= 0) return Error.Validation("Stock.InvalidQuantity", "Quantity must be greater than zero.");

        var all = batchesOfItem.ToList();

        // POS-04: the pharmacist may pick a specific batch; POS-05 / AC-POS-4 still apply to it.
        if (preferredBatchId is { } id)
        {
            var chosen = all.FirstOrDefault(b => b.Id == id);
            if (chosen is null) return Error.NotFound("Stock.BatchNotFound", "The selected batch was not found.");
            if (chosen.Status == BatchStatus.Recalled)
                return Error.Conflict("Stock.BatchRecalled", $"Batch {chosen.BatchNumber} has been recalled and cannot be sold.");
            if (!chosen.IsSellable(today))
                return Error.Conflict("Stock.BatchNotSellable", $"Batch {chosen.BatchNumber} is expired or in quarantine.");
            if (chosen.Quantity < quantity)
                return Error.Conflict("Stock.Insufficient", $"Batch {chosen.BatchNumber} has only {chosen.Quantity} unit(s).");

            return new List<BatchAllocation> { new(chosen.Id, quantity, chosen.UnitCost) };
        }

        var allocations = new List<BatchAllocation>();
        var remaining = quantity;

        foreach (var batch in all.Where(b => b.IsSellable(today)).OrderBy(b => b.ExpiryDate).ThenBy(b => b.Id))
        {
            var take = Math.Min(batch.Quantity, remaining);
            allocations.Add(new BatchAllocation(batch.Id, take, batch.UnitCost));
            remaining -= take;
            if (remaining == 0) break;
        }

        return remaining > 0
            ? Error.Conflict("Stock.Insufficient", $"Not enough stock: {quantity - remaining} of {quantity} unit(s) available.")
            : allocations;
    }
}

#endregion

#region Stock movement

/// <summary>
/// One line of the stock card (INV-12). Quantity is signed: positive in, negative out.
/// </summary>
public sealed record StockMovement(
    int BatchId, int Quantity, MovementType Type, string DocumentType, long DocumentId, string? Reason = null);

#endregion
