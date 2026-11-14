using RxSuite.Domain.Catalog;
using RxSuite.Domain.Common;
using RxSuite.Domain.Inventory;

namespace RxSuite.Domain.Sales;

// BRD 8.3 "Point of sale" and Figure 2 "POS sale flow".

#region Enums

/// <summary>POS-10 payment methods built so far. Insurance share, credit and loyalty come with their modules.</summary>
public enum PaymentMethod : byte { Cash = 1, Card = 2, MobileWallet = 3, Fawry = 4, InstaPay = 5 }

#endregion

#region Sale parts

/// <summary>Stock a sale line took from one batch, and how much of it came back through returns.</summary>
public sealed class SaleLineAllocation
{
    public int LineNumber { get; private set; }
    public int BatchId { get; private set; }
    public int Quantity { get; private set; }
    public int ReturnedQuantity { get; private set; }

    private SaleLineAllocation() { }

    public static SaleLineAllocation Create(int lineNumber, int batchId, int quantity, int returnedQuantity = 0) =>
        new() { LineNumber = lineNumber, BatchId = batchId, Quantity = quantity, ReturnedQuantity = returnedQuantity };

    public int Remaining => Quantity - ReturnedQuantity;
}

/// <summary>
/// One line of a receipt. It keeps a snapshot of the item (name, price, VAT) so the receipt
/// never changes when the item master changes later (BR-POS-4).
/// </summary>
public sealed class SaleLine
{
    private readonly List<SaleLineAllocation> _allocations = [];

    public int LineNumber { get; private set; }
    public int ItemId { get; private set; }
    public string ItemName { get; private set; } = string.Empty;
    public UnitType UnitType { get; private set; }
    public int Quantity { get; private set; }

    /// <summary>Smallest units in one sold unit, frozen at sale time (used by returns).</summary>
    public int BaseUnitsPerUnit { get; private set; }
    public decimal UnitPrice { get; private set; }
    public decimal DiscountPercent { get; private set; }
    public decimal VatRate { get; private set; }

    /// <summary>What the customer pays for the line, after line and invoice discounts, VAT included.</summary>
    public decimal LineTotal { get; private set; }
    public decimal VatAmount { get; private set; }
    public int ReturnedQuantity { get; private set; }
    public IReadOnlyList<SaleLineAllocation> Allocations => _allocations;

    private SaleLine() { }

    public static SaleLine Create(int lineNumber, Item item, UnitType unitType, int quantity, decimal unitPrice,
        decimal discountPercent, decimal invoiceDiscountPercent, IEnumerable<BatchAllocation> allocations)
    {
        if (quantity <= 0) throw new DomainException("Quantity must be greater than zero.");
        if (discountPercent is < 0 or > 100) throw new DomainException("Discount must be between 0 and 100.");

        var total = Money.Round(quantity * unitPrice * (1 - discountPercent / 100m) * (1 - invoiceDiscountPercent / 100m));

        var line = new SaleLine
        {
            LineNumber = lineNumber, ItemId = item.Id,
            ItemName = string.IsNullOrWhiteSpace(item.NameEn) ? item.NameAr : item.NameEn,
            UnitType = unitType, Quantity = quantity, BaseUnitsPerUnit = item.BaseUnitsPer(unitType),
            UnitPrice = unitPrice, DiscountPercent = discountPercent, VatRate = item.VatRate, LineTotal = total,
            // BR-POS-1: prices include VAT, so the VAT part is taken out of the total.
            VatAmount = Money.Round(total - total / (1 + item.VatRate))
        };
        line._allocations.AddRange(allocations.Select(a => SaleLineAllocation.Create(lineNumber, a.BatchId, a.Quantity)));
        return line;
    }

    /// <summary>Attaches the allocations read from the database.</summary>
    public void LoadAllocations(IEnumerable<SaleLineAllocation> allocations) => _allocations.AddRange(allocations);

    public int RemainingQuantity => Quantity - ReturnedQuantity;
}

/// <summary>One payment applied to a sale (POS-10). A sale can be split over several.</summary>
public sealed class SalePayment
{
    public PaymentMethod Method { get; private set; }

    /// <summary>The amount applied to the sale. For cash this is the tendered amount minus the change.</summary>
    public decimal Amount { get; private set; }
    public string? Reference { get; private set; }

    private SalePayment() { }

    public static SalePayment Create(PaymentMethod method, decimal amount, string? reference) =>
        new() { Method = method, Amount = amount, Reference = reference?.Trim() };
}

/// <summary>What the customer hands over, before change is worked out.</summary>
public sealed record TenderedPayment(PaymentMethod Method, decimal Amount, string? Reference);

/// <summary>Who bought. Needed for a B2B invoice (BR-ACC-3) or a very large receipt (BR-POS-6).</summary>
public sealed record Buyer(string? Name, string? NationalId, string? TaxNumber);

#endregion

#region Sale

/// <summary>
/// AGGREGATE ROOT. A completed receipt. It is created whole and never edited afterwards:
/// corrections are made with a return (BR-POS-4).
/// </summary>
public sealed class Sale : TenantEntity<long>
{
    #region State

    private readonly List<SaleLine> _lines = [];
    private readonly List<SalePayment> _payments = [];

    public int BranchId { get; private set; }
    public int ShiftId { get; private set; }
    public int CashierId { get; private set; }

    /// <summary>The licensed pharmacist who approved the Rx-only or controlled items, if any.</summary>
    public int? PharmacistId { get; private set; }

    /// <summary>BR-POS-5: unique ID given by the terminal so an offline sale is never saved twice.</summary>
    public Guid? OfflineId { get; private set; }
    public string? BuyerName { get; private set; }
    public string? BuyerNationalId { get; private set; }
    public string? BuyerTaxNumber { get; private set; }
    public decimal InvoiceDiscountPercent { get; private set; }
    public decimal Total { get; private set; }
    public decimal VatTotal { get; private set; }
    public decimal ChangeGiven { get; private set; }
    public DateTime SoldAtUtc { get; private set; }
    public IReadOnlyList<SaleLine> Lines => _lines;
    public IReadOnlyList<SalePayment> Payments => _payments;

    private Sale() { }

    #endregion

    #region Factory

    public static Sale Create(int tenantId, int branchId, int shiftId, int cashierId, int? pharmacistId,
        Guid? offlineId, Buyer? buyer, decimal invoiceDiscountPercent, IEnumerable<SaleLine> lines,
        IEnumerable<TenderedPayment> tendered, DateTime nowUtc, decimal buyerIdentificationThreshold)
    {
        var sale = new Sale
        {
            TenantId = tenantId, BranchId = branchId, ShiftId = shiftId, CashierId = cashierId,
            PharmacistId = pharmacistId, OfflineId = offlineId, BuyerName = buyer?.Name?.Trim(),
            BuyerNationalId = buyer?.NationalId?.Trim(), BuyerTaxNumber = buyer?.TaxNumber?.Trim(),
            InvoiceDiscountPercent = invoiceDiscountPercent, SoldAtUtc = nowUtc
        };
        sale._lines.AddRange(lines);

        if (sale._lines.Count == 0) throw new DomainException("A sale needs at least one item.");
        if (invoiceDiscountPercent is < 0 or > 100) throw new DomainException("Discount must be between 0 and 100.");

        sale.Total = sale._lines.Sum(l => l.LineTotal);
        sale.VatTotal = sale._lines.Sum(l => l.VatAmount);

        // BR-POS-6: a very large receipt must identify the buyer.
        if (sale.Total >= buyerIdentificationThreshold
            && (string.IsNullOrWhiteSpace(sale.BuyerName) || string.IsNullOrWhiteSpace(sale.BuyerNationalId)))
            throw new DomainException("This receipt is large enough to require the buyer's name and national ID.");

        sale.ApplyPayments(tendered.ToList());
        return sale;
    }

    /// <summary>
    /// POS-10: split payment with change. Only cash can exceed what is due, and the excess is
    /// handed back as change.
    /// </summary>
    private void ApplyPayments(List<TenderedPayment> tendered)
    {
        if (tendered.Count == 0) throw new DomainException("A sale needs at least one payment.");
        if (tendered.Any(p => p.Amount <= 0)) throw new DomainException("Payment amounts must be greater than zero.");

        var nonCash = tendered.Where(p => p.Method != PaymentMethod.Cash).Sum(p => p.Amount);
        var cash = tendered.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount);

        if (nonCash > Total) throw new DomainException("Card and wallet payments cannot exceed the receipt total.");
        if (nonCash + cash < Total)
            throw new DomainException($"Payment is short by {Total - nonCash - cash:0.00} EGP.");

        ChangeGiven = nonCash + cash - Total;

        _payments.AddRange(tendered.Where(p => p.Method != PaymentMethod.Cash)
            .Select(p => SalePayment.Create(p.Method, p.Amount, p.Reference)));
        if (cash - ChangeGiven > 0)
            _payments.Add(SalePayment.Create(PaymentMethod.Cash, cash - ChangeGiven, null));
    }

    /// <summary>Attaches the children read from the database.</summary>
    public void Load(IEnumerable<SaleLine> lines, IEnumerable<SalePayment> payments)
    {
        _lines.AddRange(lines);
        _payments.AddRange(payments);
    }

    #endregion

    #region Derived values

    /// <summary>BR-ACC-3: a buyer with a tax registration number gets a B2B e-invoice, not an e-receipt.</summary>
    public bool IsBusinessSale => !string.IsNullOrWhiteSpace(BuyerTaxNumber);

    /// <summary>Stock to take out: one entry per batch, in the smallest unit.</summary>
    public IEnumerable<StockMovement> StockMovements() => _lines
        .SelectMany(l => l.Allocations)
        .GroupBy(a => a.BatchId)
        .Select(g => new StockMovement(g.Key, -g.Sum(a => a.Quantity), MovementType.Sale, "Sale", Id));

    #endregion
}

#endregion
