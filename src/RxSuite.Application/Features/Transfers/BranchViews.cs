using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Domain.Catalog;
using RxSuite.Domain.Common;
using RxSuite.Domain.Identity;
using RxSuite.Domain.Inventory;
using RxSuite.Domain.Rules;

namespace RxSuite.Application.Features.Transfers;

// USE CASES (BRD 8.8): per-branch price and stock settings (MBR-02, US-MBR-1), the stock matrix
// (MBR-05, US-MBR-3), rebalancing suggestions (MBR-06, US-MBR-4) and the branch comparison (MBR-08).

#region Read models

/// <summary>One batch of one item in one location, as the matrix procedure returns it.</summary>
public sealed class StockMatrixRow
{
    public int ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int BranchId { get; set; }
    public string BranchName { get; set; } = string.Empty;
    public string BatchNumber { get; set; } = string.Empty;
    public DateOnly ExpiryDate { get; set; }
    public int Quantity { get; set; }
    public BatchStatus Status { get; set; }
}

/// <summary>Stock of an item on its way to a location (BR-MBR-1).</summary>
public sealed class InTransitRow
{
    public int ItemId { get; set; }
    public int ToBranchId { get; set; }
    public int Quantity { get; set; }
}

public sealed record BatchStockDto(string BatchNumber, DateOnly ExpiryDate, int Quantity, BatchStatus Status);

public sealed record BranchStockDto(int BranchId, string BranchName, int Sellable, int NotSellable, int InTransitHere,
    DateOnly? NearestExpiry, IReadOnlyList<BatchStockDto> Batches);

/// <summary>MBR-05: one item across every location. BR-MBR-1: in transit counts in the total, not in any branch.</summary>
public sealed record ItemStockDto(int ItemId, string ItemName, int TenantTotal, int InTransit, IReadOnlyList<BranchStockDto> Branches);

public sealed class RebalancingSuggestionDto
{
    public int ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int FromBranchId { get; set; }
    public string FromBranchName { get; set; } = string.Empty;
    public string BatchNumber { get; set; } = string.Empty;
    public DateOnly ExpiryDate { get; set; }
    public int AvailableQuantity { get; set; }
    public int FromBranchSales { get; set; }
    public int ToBranchId { get; set; }
    public string ToBranchName { get; set; } = string.Empty;
    public int ToBranchSales { get; set; }

    /// <summary>NearExpiry or Overstock.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>What the receiving branch sold in the window, capped at what is available.</summary>
    public int SuggestedQuantity { get; set; }
}

/// <summary>MBR-08: one branch over a period. Money in EGP; stock days at the period's cost of sales.</summary>
public sealed class BranchPerformanceDto
{
    public int BranchId { get; set; }
    public string BranchName { get; set; } = string.Empty;
    public decimal SalesTotal { get; set; }
    public decimal ReturnsTotal { get; set; }
    public decimal VatTotal { get; set; }
    public decimal CostOfSales { get; set; }
    public decimal StockValue { get; set; }
    public decimal ExpiryLosses { get; set; }
    public int Days { get; set; }

    public decimal NetSales => SalesTotal - ReturnsTotal;

    /// <summary>Net sales without VAT, minus what the goods cost.</summary>
    public decimal GrossMargin => Money.Round(NetSales - VatTotal - CostOfSales);
    public decimal? MarginPercent => NetSales - VatTotal > 0 ? Math.Round(100 * GrossMargin / (NetSales - VatTotal), 1) : null;

    /// <summary>How many days the current stock lasts at this period's pace.</summary>
    public decimal? StockDays => CostOfSales > 0 && Days > 0 ? Math.Round(StockValue / (CostOfSales / Days), 1) : null;
}

#endregion

#region Command: branch price, min/max, visibility (MBR-02)

/// <summary>Leave a field empty to use the central value. Hidden items cannot be sold in the branch.</summary>
[RequiresPermission(Permissions.PricesChange)]
public sealed record SetBranchItemSettingCommand(int BranchId, int ItemId, decimal? SellingPrice, int? MinStock, int? MaxStock,
    bool IsHidden) : ICommand<Unit>;

internal sealed class SetBranchItemSettingHandler(IBranchItemSettingRepository settings, IItemRepository items,
    ICurrentUser currentUser, IAuditTrail audit) : IRequestHandler<SetBranchItemSettingCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(SetBranchItemSettingCommand c, CancellationToken ct)
    {
        if (!currentUser.CanAccessBranch(c.BranchId)) return Errors.BranchNotAllowed;

        var item = await items.GetByIdAsync(c.ItemId, ct);
        if (item is null) return Errors.NotFound("Item", c.ItemId);

        var old = (await settings.GetAsync(c.BranchId, [item.Id], ct)).FirstOrDefault();
        var setting = BranchItemSetting.Create(currentUser.TenantId, c.BranchId, item, c.SellingPrice, c.MinStock, c.MaxStock,
            c.IsHidden, allowAboveOfficialPrice: currentUser.Has(Permissions.PricesAboveOfficial));
        await settings.SaveAsync(setting, ct);

        // AC-USR-3: price changes keep old and new value, user and time.
        await audit.RecordAsync("Catalog", "BranchPriceChanged", item.Id,
            oldValue: (old?.SellingPrice ?? item.SellingPrice).ToString("0.00"),
            newValue: (setting.SellingPrice ?? item.SellingPrice).ToString("0.00") + (setting.IsHidden ? " (hidden)" : ""),
            branchId: c.BranchId, ct: ct);
        return Unit.Value;
    }
}

#endregion

#region Query: stock matrix (MBR-05)

/// <summary>US-MBR-3: an item, or items matching a search, in every location the user can see.</summary>
[RequiresPermission(Permissions.BranchReports)]
public sealed record GetStockMatrixQuery(string Term) : IQuery<IReadOnlyList<ItemStockDto>>;

internal sealed class GetStockMatrixHandler(IBranchReportRepository reports, ICurrentUser currentUser, IClock clock)
    : IRequestHandler<GetStockMatrixQuery, IReadOnlyList<ItemStockDto>>
{
    public async Task<Result<IReadOnlyList<ItemStockDto>>> HandleAsync(GetStockMatrixQuery q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q.Term) || q.Term.Trim().Length < 2)
            return Error.Validation("Matrix.Term", "Type at least two characters: a code, a barcode or a name.");

        var (batches, inTransit) = await reports.GetStockMatrixAsync(q.Term.Trim(), ct);
        var today = clock.Today;

        return batches.GroupBy(r => (r.ItemId, r.ItemName)).Select(item =>
        {
            var transit = inTransit.Where(t => t.ItemId == item.Key.ItemId).ToList();
            var branches = item.Where(r => currentUser.CanAccessBranch(r.BranchId))
                .GroupBy(r => (r.BranchId, r.BranchName))
                .Select(b =>
                {
                    var sellable = b.Where(r => r.Status == BatchStatus.Available && r.ExpiryDate >= today).ToList();
                    return new BranchStockDto(b.Key.BranchId, b.Key.BranchName, sellable.Sum(r => r.Quantity),
                        b.Sum(r => r.Quantity) - sellable.Sum(r => r.Quantity),
                        transit.Where(t => t.ToBranchId == b.Key.BranchId).Sum(t => t.Quantity),
                        sellable.Count == 0 ? null : sellable.Min(r => r.ExpiryDate),
                        b.OrderBy(r => r.ExpiryDate).Select(r => new BatchStockDto(r.BatchNumber, r.ExpiryDate, r.Quantity, r.Status)).ToList());
                })
                .OrderBy(b => b.BranchName).ToList();

            return new ItemStockDto(item.Key.ItemId, item.Key.ItemName, item.Sum(r => r.Quantity) + transit.Sum(t => t.Quantity),
                transit.Sum(t => t.Quantity), branches);
        }).ToList();
    }
}

#endregion

#region Query: rebalancing suggestions (MBR-06)

/// <summary>
/// US-MBR-4: near-expiry or overstocked batches in a branch that sells the item slowly, next to the
/// branch that sells it fastest. A suggestion only: a direct transfer carries it out.
/// </summary>
[RequiresPermission(Permissions.BranchReports)]
public sealed record GetRebalancingSuggestionsQuery(int SalesWindowDays = 30) : IQuery<IReadOnlyList<RebalancingSuggestionDto>>;

internal sealed class GetRebalancingSuggestionsHandler(IBranchReportRepository reports, ICurrentUser currentUser, IClock clock,
    BusinessRules rules) : IRequestHandler<GetRebalancingSuggestionsQuery, IReadOnlyList<RebalancingSuggestionDto>>
{
    public async Task<Result<IReadOnlyList<RebalancingSuggestionDto>>> HandleAsync(GetRebalancingSuggestionsQuery q, CancellationToken ct)
    {
        if (q.SalesWindowDays is < 7 or > 365) return Error.Validation("Rebalance.Window", "The sales window must be 7 to 365 days.");

        var suggestions = await reports.GetRebalancingSuggestionsAsync(clock.Today,
            clock.Today.AddDays(rules.Inventory.NearExpiryDays), q.SalesWindowDays, ct);
        return suggestions.Where(s => currentUser.CanAccessBranch(s.FromBranchId) && currentUser.CanAccessBranch(s.ToBranchId)).ToList();
    }
}

#endregion

#region Query: branch comparison (MBR-08)

[RequiresPermission(Permissions.BranchReports)]
public sealed record GetBranchPerformanceQuery(DateOnly From, DateOnly To) : IQuery<IReadOnlyList<BranchPerformanceDto>>;

internal sealed class GetBranchPerformanceHandler(IBranchReportRepository reports, ICurrentUser currentUser)
    : IRequestHandler<GetBranchPerformanceQuery, IReadOnlyList<BranchPerformanceDto>>
{
    public async Task<Result<IReadOnlyList<BranchPerformanceDto>>> HandleAsync(GetBranchPerformanceQuery q, CancellationToken ct)
    {
        if (q.To < q.From || q.To.DayNumber - q.From.DayNumber > 366)
            return Error.Validation("Performance.Dates", "Choose a period of up to one year, start before end.");

        return (await reports.GetBranchPerformanceAsync(q.From, q.To, ct)).Where(b => currentUser.CanAccessBranch(b.BranchId)).ToList();
    }
}

#endregion
