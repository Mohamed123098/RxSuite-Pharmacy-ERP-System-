using RxSuite.Domain.Common;

namespace RxSuite.Domain.Catalog;

// BRD 8.8 MBR-02 / US-MBR-1: one central catalogue and price, with optional overrides per branch.

/// <summary>
/// What one branch does differently for one item: its own price, its own min/max stock, or not
/// selling it at all. Every field left empty falls back to the central item.
/// </summary>
public sealed class BranchItemSetting : TenantEntity<int>
{
    public int BranchId { get; private set; }
    public int ItemId { get; private set; }

    /// <summary>Box price in this branch, VAT included. Null: the central price.</summary>
    public decimal? SellingPrice { get; private set; }

    /// <summary>Reorder level in this branch, smallest unit. Null: the item's reorder level.</summary>
    public int? MinStock { get; private set; }

    /// <summary>Above this the branch is overstocked (MBR-06), smallest unit. Null: no maximum.</summary>
    public int? MaxStock { get; private set; }

    /// <summary>The item is not sold in this branch.</summary>
    public bool IsHidden { get; private set; }

    private BranchItemSetting() { }

    public static BranchItemSetting Create(int tenantId, int branchId, Item item, decimal? sellingPrice, int? minStock,
        int? maxStock, bool isHidden, bool allowAboveOfficialPrice)
    {
        if (sellingPrice <= 0) throw new DomainException("The selling price must be greater than zero.");
        // BR-POS-2 applies to branch prices too.
        if (sellingPrice is { } price && item.OfficialPrice is { } official && price > official && !allowAboveOfficialPrice)
            throw new DomainException($"The selling price cannot exceed the official price ({official:0.00} EGP).");
        if (minStock < 0 || maxStock < 0) throw new DomainException("Stock levels cannot be negative.");
        if (minStock is { } min && maxStock is { } max && min > max)
            throw new DomainException("The minimum stock cannot be above the maximum.");

        return new BranchItemSetting
        {
            TenantId = tenantId, BranchId = branchId, ItemId = item.Id, SellingPrice = sellingPrice,
            MinStock = minStock, MaxStock = maxStock, IsHidden = isHidden
        };
    }
}
