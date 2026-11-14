using RxSuite.Domain.Common;

namespace RxSuite.Domain.Purchasing;

// BRD 8.5 "Purchasing and suppliers". Purchases are made in boxes.

#region Enums

public enum SupplierType : byte { Distributor = 1, Manufacturer = 2, Other = 3 }

/// <summary>BRD key data "Purchase order" statuses.</summary>
public enum PurchaseOrderStatus : byte
{
    Draft = 1, PendingApproval = 2, Approved = 3, Sent = 4, PartiallyReceived = 5, Closed = 6, Cancelled = 7
}

#endregion

#region Supplier

/// <summary>PUR-01: a distributor or manufacturer the tenant buys from.</summary>
public sealed class Supplier : TenantEntity<int>
{
    public string NameEn { get; private set; } = string.Empty;
    public string NameAr { get; private set; } = string.Empty;
    public SupplierType Type { get; private set; }
    public string TaxNumber { get; private set; } = string.Empty;
    public string Phone { get; private set; } = string.Empty;
    public int PaymentTermDays { get; private set; }
    public decimal CreditLimit { get; private set; }

    /// <summary>What the tenant owes this supplier (PUR-10).</summary>
    public decimal Balance { get; private set; }
    public bool IsActive { get; private set; }

    private Supplier() { }

    public static Supplier Create(int tenantId, string nameEn, string nameAr, SupplierType type, string taxNumber,
        string phone, int paymentTermDays, decimal creditLimit)
    {
        if (string.IsNullOrWhiteSpace(nameEn) && string.IsNullOrWhiteSpace(nameAr))
            throw new DomainException("The supplier name is required.");
        if (paymentTermDays < 0 || creditLimit < 0)
            throw new DomainException("Payment terms and credit limit cannot be negative.");

        return new Supplier
        {
            TenantId = tenantId, NameEn = nameEn.Trim(), NameAr = nameAr.Trim(), Type = type,
            TaxNumber = taxNumber.Trim(), Phone = phone.Trim(), PaymentTermDays = paymentTermDays,
            CreditLimit = creditLimit, IsActive = true
        };
    }

    /// <summary>BR-PUR-4: a new order that takes the supplier over its credit limit raises a warning.</summary>
    public bool WouldExceedCreditLimit(decimal orderTotal) => CreditLimit > 0 && Balance + orderTotal > CreditLimit;
}

#endregion

#region Purchase order

/// <summary>One line of a purchase order (PUR-03).</summary>
public sealed class PurchaseOrderLine
{
    public int ItemId { get; private set; }
    public int QuantityBoxes { get; private set; }
    public int BonusBoxes { get; private set; }
    public decimal UnitCost { get; private set; }
    public decimal DiscountPercent { get; private set; }
    public int ReceivedBoxes { get; private set; }

    private PurchaseOrderLine() { }

    public static PurchaseOrderLine Create(int itemId, int quantityBoxes, int bonusBoxes, decimal unitCost, decimal discountPercent)
    {
        if (quantityBoxes <= 0) throw new DomainException("Ordered quantity must be greater than zero.");
        if (bonusBoxes < 0 || unitCost < 0) throw new DomainException("Bonus and cost cannot be negative.");
        if (discountPercent is < 0 or > 100) throw new DomainException("Discount must be between 0 and 100.");

        return new PurchaseOrderLine
        {
            ItemId = itemId, QuantityBoxes = quantityBoxes, BonusBoxes = bonusBoxes, UnitCost = unitCost,
            DiscountPercent = discountPercent
        };
    }

    public decimal LineTotal => Money.Round(QuantityBoxes * UnitCost * (1 - DiscountPercent / 100m));
    public bool IsFullyReceived => ReceivedBoxes >= QuantityBoxes + BonusBoxes;

    internal void AddReceived(int boxes) => ReceivedBoxes += boxes;
}

/// <summary>
/// AGGREGATE ROOT. A purchase order and its status flow:
/// Draft -> (PendingApproval ->) Approved -> Sent -> PartiallyReceived -> Closed.
/// </summary>
public sealed class PurchaseOrder : TenantEntity<int>
{
    #region State

    private readonly List<PurchaseOrderLine> _lines = [];

    public int SupplierId { get; private set; }
    public int BranchId { get; private set; }
    public PurchaseOrderStatus Status { get; private set; }
    public DateOnly? ExpectedDeliveryDate { get; private set; }
    public string Notes { get; private set; } = string.Empty;
    public decimal Total { get; private set; }
    public int CreatedBy { get; private set; }
    public int? ApprovedBy { get; private set; }
    public IReadOnlyList<PurchaseOrderLine> Lines => _lines;

    private PurchaseOrder() { }

    #endregion

    #region Factory

    public static PurchaseOrder Create(int tenantId, int supplierId, int branchId, int createdBy,
        DateOnly? expectedDeliveryDate, string? notes, IEnumerable<PurchaseOrderLine> lines)
    {
        var order = new PurchaseOrder
        {
            TenantId = tenantId, SupplierId = supplierId, BranchId = branchId, CreatedBy = createdBy,
            ExpectedDeliveryDate = expectedDeliveryDate, Notes = notes?.Trim() ?? string.Empty,
            Status = PurchaseOrderStatus.Draft
        };
        order._lines.AddRange(lines);

        if (order._lines.Count == 0) throw new DomainException("A purchase order needs at least one line.");
        if (order._lines.GroupBy(l => l.ItemId).Any(g => g.Count() > 1))
            throw new DomainException("An item can appear only once in a purchase order.");

        order.Total = order._lines.Sum(l => l.LineTotal);
        return order;
    }

    /// <summary>Attaches the lines read from the database.</summary>
    public void LoadLines(IEnumerable<PurchaseOrderLine> lines) => _lines.AddRange(lines);

    #endregion

    #region Behaviour

    /// <summary>PUR-04 / AC-PUR-1: an order above the approval limit waits for approval.</summary>
    public void Submit(decimal approvalLimit)
    {
        if (Status != PurchaseOrderStatus.Draft) throw new DomainException("Only a draft order can be submitted.");
        Status = Total > approvalLimit ? PurchaseOrderStatus.PendingApproval : PurchaseOrderStatus.Approved;
    }

    public void Approve(int approverId)
    {
        if (Status != PurchaseOrderStatus.PendingApproval)
            throw new DomainException("Only an order waiting for approval can be approved.");
        Status = PurchaseOrderStatus.Approved;
        ApprovedBy = approverId;
    }

    public bool CanReceive => Status is PurchaseOrderStatus.Approved or PurchaseOrderStatus.Sent
        or PurchaseOrderStatus.PartiallyReceived;

    /// <summary>BR-PUR-3: an order can be received in parts; it closes when every line is complete.</summary>
    public void RegisterReceipt(int itemId, int boxesIncludingBonus)
    {
        if (!CanReceive) throw new DomainException("This purchase order cannot receive goods in its current status.");

        var line = _lines.FirstOrDefault(l => l.ItemId == itemId)
            ?? throw new DomainException("A received item is not on the purchase order.");

        line.AddReceived(boxesIncludingBonus);
        Status = _lines.All(l => l.IsFullyReceived) ? PurchaseOrderStatus.Closed : PurchaseOrderStatus.PartiallyReceived;
    }

    #endregion
}

#endregion

#region Goods receipt

/// <summary>One received line: an item, its batch and expiry, and what it cost (PUR-06).</summary>
public sealed class GoodsReceiptLine
{
    public int ItemId { get; private set; }
    public string BatchNumber { get; private set; } = string.Empty;
    public DateOnly ExpiryDate { get; private set; }
    public int QuantityBoxes { get; private set; }
    public int BonusBoxes { get; private set; }
    public decimal CostPerBox { get; private set; }
    public decimal DiscountPercent { get; private set; }
    public decimal VatRate { get; private set; }
    public string? ShelfLifeOverrideReason { get; private set; }

    private GoodsReceiptLine() { }

    public static GoodsReceiptLine Create(int itemId, string batchNumber, DateOnly expiryDate, int quantityBoxes,
        int bonusBoxes, decimal costPerBox, decimal discountPercent, decimal vatRate,
        string? shelfLifeOverrideReason, DateOnly today, int minimumShelfLifeMonths)
    {
        if (string.IsNullOrWhiteSpace(batchNumber)) throw new DomainException("The batch number is required.");
        if (quantityBoxes <= 0) throw new DomainException("Received quantity must be greater than zero.");
        if (bonusBoxes < 0 || costPerBox < 0) throw new DomainException("Bonus and cost cannot be negative.");
        if (discountPercent is < 0 or > 100) throw new DomainException("Discount must be between 0 and 100.");
        if (expiryDate <= today) throw new DomainException($"Batch {batchNumber} is already expired.");

        // PUR-07 / AC-PUR-3: short shelf life is accepted only with a reason.
        if (expiryDate < today.AddMonths(minimumShelfLifeMonths) && string.IsNullOrWhiteSpace(shelfLifeOverrideReason))
            throw new DomainException(
                $"Batch {batchNumber} expires in less than {minimumShelfLifeMonths} months. Give a reason to accept it.");

        return new GoodsReceiptLine
        {
            ItemId = itemId, BatchNumber = batchNumber.Trim(), ExpiryDate = expiryDate,
            QuantityBoxes = quantityBoxes, BonusBoxes = bonusBoxes, CostPerBox = costPerBox,
            DiscountPercent = discountPercent, VatRate = vatRate,
            ShelfLifeOverrideReason = shelfLifeOverrideReason?.Trim()
        };
    }

    public int TotalBoxes => QuantityBoxes + BonusBoxes;

    /// <summary>What is paid for the line before VAT.</summary>
    public decimal NetCost => Money.Round(QuantityBoxes * CostPerBox * (1 - DiscountPercent / 100m));
    public decimal VatAmount => Money.Round(NetCost * VatRate);

    /// <summary>
    /// BR-PUR-1 / AC-PUR-2: real cost of a box = (line cost - discounts) / (paid + bonus quantity).
    /// 10 boxes + 1 bonus at 100 EGP gives 90.91 EGP per box.
    /// </summary>
    public decimal EffectiveCostPerBox => NetCost / TotalBoxes;
}

/// <summary>AGGREGATE ROOT. Goods received from a supplier, against an order or directly (PUR-06).</summary>
public sealed class GoodsReceipt : TenantEntity<int>
{
    private readonly List<GoodsReceiptLine> _lines = [];

    public int SupplierId { get; private set; }
    public int BranchId { get; private set; }
    public int? PurchaseOrderId { get; private set; }
    public string SupplierInvoiceNumber { get; private set; } = string.Empty;
    public int ReceivedBy { get; private set; }
    public decimal Total { get; private set; }
    public IReadOnlyList<GoodsReceiptLine> Lines => _lines;

    private GoodsReceipt() { }

    public static GoodsReceipt Create(int tenantId, int supplierId, int branchId, int? purchaseOrderId,
        string? supplierInvoiceNumber, int receivedBy, IEnumerable<GoodsReceiptLine> lines)
    {
        var receipt = new GoodsReceipt
        {
            TenantId = tenantId, SupplierId = supplierId, BranchId = branchId, PurchaseOrderId = purchaseOrderId,
            SupplierInvoiceNumber = supplierInvoiceNumber?.Trim() ?? string.Empty, ReceivedBy = receivedBy
        };
        receipt._lines.AddRange(lines);

        if (receipt._lines.Count == 0) throw new DomainException("A goods receipt needs at least one line.");

        receipt.Total = receipt._lines.Sum(l => l.NetCost + l.VatAmount);
        return receipt;
    }
}

#endregion
