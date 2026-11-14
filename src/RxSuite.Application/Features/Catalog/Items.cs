using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Domain.Catalog;
using RxSuite.Domain.Common;
using RxSuite.Domain.Identity;

namespace RxSuite.Application.Features.Catalog;

// USE CASES: the item master (INV-01, INV-02), price changes (BR-POS-2, AC-USR-3) and the POS
// item search (POS-01, POS-02).

#region Read model

/// <summary>One row of the POS search (POS-02): price, stock in this branch and nearest expiry.</summary>
public sealed class ItemSearchResult
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string ScientificName { get; set; } = string.Empty;
    public string Strength { get; set; } = string.Empty;
    public string DosageForm { get; set; } = string.Empty;
    public decimal SellingPrice { get; set; }
    public int UnitsPerBox { get; set; }

    /// <summary>Sellable stock in the branch, in the smallest unit (BR-INV-1).</summary>
    public int AvailableQuantity { get; set; }
    public DateOnly? NearestExpiry { get; set; }
    public bool IsRxOnly { get; set; }
    public bool IsControlled { get; set; }
}

#endregion

#region Command: create an item

[RequiresPermission(Permissions.ItemsManage)]
public sealed record CreateItemCommand(string Code, string NameEn, string NameAr, string ScientificName,
    string Strength, string DosageForm, int StripsPerBox, int UnitsPerStrip, decimal? OfficialPrice,
    decimal SellingPrice, decimal VatRate, string? EtaItemCode, bool IsRxOnly, bool IsControlled,
    int ReorderLevel, IReadOnlyList<string>? Barcodes) : ICommand<int>;

internal sealed class CreateItemHandler(IItemRepository items, ICurrentUser currentUser, IAuditTrail audit)
    : IRequestHandler<CreateItemCommand, int>
{
    public async Task<Result<int>> HandleAsync(CreateItemCommand c, CancellationToken ct)
    {
        if (await items.CodeExistsAsync(c.Code.Trim(), ct))
            return Error.Conflict("Item.CodeTaken", $"Item code '{c.Code}' is already used.");

        var item = Item.Create(currentUser.TenantId, c.Code, c.NameEn, c.NameAr, c.ScientificName, c.Strength,
            c.DosageForm, c.StripsPerBox, c.UnitsPerStrip, c.OfficialPrice, c.SellingPrice, c.VatRate,
            c.EtaItemCode, c.IsRxOnly, c.IsControlled, c.ReorderLevel,
            allowAboveOfficialPrice: currentUser.Has(Permissions.PricesAboveOfficial));

        var barcodes = (c.Barcodes ?? []).Select(b => b.Trim()).Where(b => b.Length > 0).Distinct();
        var id = await items.AddAsync(item, barcodes, ct);

        await audit.RecordAsync("Catalog", "ItemCreated", id, newValue: item.Code, ct: ct);
        return id;
    }
}

#endregion

#region Command: change the selling price

/// <summary>AC-USR-3: a price change stores the old price, the new price, the user and the time.</summary>
[RequiresPermission(Permissions.PricesChange)]
public sealed record ChangeItemPriceCommand(int ItemId, decimal NewPrice, string? Reason) : ICommand<Unit>;

internal sealed class ChangeItemPriceHandler(IItemRepository items, ICurrentUser currentUser, IAuditTrail audit)
    : IRequestHandler<ChangeItemPriceCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(ChangeItemPriceCommand c, CancellationToken ct)
    {
        var item = await items.GetByIdAsync(c.ItemId, ct);
        if (item is null) return Errors.NotFound("Item", c.ItemId);

        var oldPrice = item.SellingPrice;
        item.ChangePrice(c.NewPrice, currentUser.Has(Permissions.PricesAboveOfficial));   // BR-POS-2

        await items.UpdatePriceAsync(item, ct);
        await audit.RecordAsync("Catalog", "PriceChanged", item.Id, oldPrice.ToString("0.00"),
            item.SellingPrice.ToString("0.00"), c.Reason, ct: ct);
        return Unit.Value;
    }
}

#endregion

#region Query: POS item search

/// <summary>Search by barcode, code, trade name or scientific name, Arabic or English (POS-01).</summary>
[RequiresPermission(Permissions.InventoryView)]
public sealed record SearchItemsQuery(int BranchId, string Term) : IQuery<IReadOnlyList<ItemSearchResult>>;

internal sealed class SearchItemsHandler(IItemRepository items, ICurrentUser currentUser, IClock clock)
    : IRequestHandler<SearchItemsQuery, IReadOnlyList<ItemSearchResult>>
{
    public async Task<Result<IReadOnlyList<ItemSearchResult>>> HandleAsync(SearchItemsQuery q, CancellationToken ct)
    {
        if (!currentUser.CanAccessBranch(q.BranchId)) return Errors.BranchNotAllowed;
        if (string.IsNullOrWhiteSpace(q.Term))
            return Error.Validation("Search.Empty", "Type or scan something to search for.");

        return Result<IReadOnlyList<ItemSearchResult>>.Success(
            await items.SearchAsync(q.BranchId, q.Term.Trim(), clock.Today, ct));
    }
}

#endregion
