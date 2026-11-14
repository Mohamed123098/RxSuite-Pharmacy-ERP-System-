using RxSuite.Domain.Common;
using RxSuite.Domain.Inventory;

namespace RxSuite.Domain.Sales;

// BRD 8.3 POS-14 and Figure 12 "Returns and exchange flow".

#region Enums and requests

public enum RefundMethod : byte { Cash = 1, OriginalMethod = 2, StoreCredit = 3 }

/// <summary>What the customer brings back: a receipt line, how many, and whether it can be sold again.</summary>
public sealed record ReturnRequest(int LineNumber, int Quantity, bool IsResaleable);

#endregion

#region Return parts

/// <summary>Stock put back into one batch by a return line.</summary>
public sealed record BatchRestoration(int LineNumber, int BatchId, int Quantity, bool IsResaleable);

/// <summary>One returned receipt line and its refund.</summary>
public sealed record SaleReturnLine(int LineNumber, int Quantity, bool IsResaleable, decimal RefundAmount);

#endregion

#region Sale return

/// <summary>
/// AGGREGATE ROOT. A return against an original receipt. It decides the refund and where the
/// stock goes: back to its original batch when resaleable, otherwise to quarantine.
/// </summary>
public sealed class SaleReturn : TenantEntity<long>
{
    #region State

    private readonly List<SaleReturnLine> _lines = [];
    private readonly List<BatchRestoration> _restorations = [];

    public long SaleId { get; private set; }
    public int BranchId { get; private set; }
    public int ShiftId { get; private set; }
    public int CashierId { get; private set; }
    public RefundMethod RefundMethod { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public decimal RefundTotal { get; private set; }
    public DateTime ReturnedAtUtc { get; private set; }
    public IReadOnlyList<SaleReturnLine> Lines => _lines;
    public IReadOnlyList<BatchRestoration> Restorations => _restorations;

    private SaleReturn() { }

    #endregion

    #region Factory

    public static SaleReturn Create(Sale sale, IReadOnlyCollection<ReturnRequest> requests, RefundMethod refundMethod,
        string reason, int shiftId, int cashierId, DateTime nowUtc, int returnPolicyDays)
    {
        if (requests.Count == 0) throw new DomainException("A return needs at least one line.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("The reason for the return is required.");
        if (requests.GroupBy(r => r.LineNumber).Any(g => g.Count() > 1))
            throw new DomainException("A receipt line can appear only once in a return.");

        // Figure 12 "Within return policy?": refuse returns after the allowed number of days.
        if (nowUtc > sale.SoldAtUtc.AddDays(returnPolicyDays))
            throw new DomainException($"The return period of {returnPolicyDays} days has passed for this receipt.");

        var saleReturn = new SaleReturn
        {
            TenantId = sale.TenantId, SaleId = sale.Id, BranchId = sale.BranchId, ShiftId = shiftId,
            CashierId = cashierId, RefundMethod = refundMethod, Reason = reason.Trim(), ReturnedAtUtc = nowUtc
        };

        foreach (var request in requests)
        {
            var line = sale.Lines.FirstOrDefault(l => l.LineNumber == request.LineNumber)
                ?? throw new DomainException($"Line {request.LineNumber} is not on the receipt.");

            if (request.Quantity <= 0 || request.Quantity > line.RemainingQuantity)
                throw new DomainException(
                    $"Line {line.LineNumber}: only {line.RemainingQuantity} can still be returned.");

            // The refund is the returned share of what was actually paid for the line.
            var refund = Money.Round(line.LineTotal * request.Quantity / line.Quantity);
            saleReturn._lines.Add(new SaleReturnLine(line.LineNumber, request.Quantity, request.IsResaleable, refund));
            saleReturn.RestoreStock(line, request);
        }

        saleReturn.RefundTotal = saleReturn._lines.Sum(l => l.RefundAmount);
        return saleReturn;
    }

    /// <summary>Spreads the returned quantity over the batches the line was sold from.</summary>
    private void RestoreStock(SaleLine line, ReturnRequest request)
    {
        var remaining = request.Quantity * line.BaseUnitsPerUnit;

        foreach (var allocation in line.Allocations.Where(a => a.Remaining > 0))
        {
            var quantity = Math.Min(allocation.Remaining, remaining);
            _restorations.Add(new BatchRestoration(line.LineNumber, allocation.BatchId, quantity, request.IsResaleable));
            remaining -= quantity;
            if (remaining == 0) break;
        }

        if (remaining > 0) throw new DomainException($"Line {line.LineNumber}: the returned quantity exceeds what was sold.");
    }

    #endregion

    #region Derived values

    /// <summary>Stock to put back. Only resaleable stock returns to its original, sellable batch.</summary>
    public IEnumerable<StockMovement> StockMovements() => _restorations
        .Where(r => r.IsResaleable)
        .GroupBy(r => r.BatchId)
        .Select(g => new StockMovement(g.Key, g.Sum(r => r.Quantity), MovementType.SaleReturn, "SaleReturn", Id));

    #endregion
}

#endregion
