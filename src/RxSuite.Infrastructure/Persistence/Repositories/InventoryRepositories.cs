using RxSuite.Application.Abstractions;
using RxSuite.Application.Features.Catalog;
using RxSuite.Application.Features.Inventory;
using RxSuite.Domain.Catalog;
using RxSuite.Domain.Inventory;
using RxSuite.Infrastructure.Persistence.Connection;

namespace RxSuite.Infrastructure.Persistence.Repositories;

#region Items

/// <summary>REPOSITORY for the item master.</summary>
internal sealed class ItemRepository(IDbSession session, ICurrentUser currentUser)
    : StoredProcedureRepository(session, currentUser), IItemRepository
{
    #region Commands

    public async Task<int> AddAsync(Item i, IEnumerable<string> barcodes, CancellationToken ct)
    {
        var id = await ExecuteScalarAsync<int>("dbo.usp_Item_Insert", Scoped(new
        {
            i.Code, i.NameEn, i.NameAr, i.ScientificName, i.Strength, i.DosageForm, i.StripsPerBox,
            i.UnitsPerStrip, i.OfficialPrice, i.SellingPrice, i.VatRate, i.EtaItemCode, i.IsRxOnly,
            i.IsControlled, i.ReorderLevel, BarcodesJson = Json(barcodes)
        }), ct);

        i.AssignId(id);
        return id;
    }

    public Task UpdatePriceAsync(Item item, CancellationToken ct) =>
        ExecuteAsync("dbo.usp_Item_UpdatePrice", Scoped(new { ItemId = item.Id, item.SellingPrice }), ct);

    #endregion

    #region Queries

    public Task<bool> CodeExistsAsync(string code, CancellationToken ct) =>
        ExecuteScalarAsync<bool>("dbo.usp_Item_CodeExists", Scoped(new { Code = code }), ct);

    public Task<Item?> GetByIdAsync(int itemId, CancellationToken ct) =>
        QuerySingleOrDefaultAsync<Item>("dbo.usp_Item_GetById", Scoped(new { ItemId = itemId }), ct);

    public Task<IReadOnlyList<Item>> GetByIdsAsync(IEnumerable<int> itemIds, CancellationToken ct) =>
        QueryAsync<Item>("dbo.usp_Item_GetByIds", Scoped(new { ItemIdsJson = Json(itemIds) }), ct);

    public Task<IReadOnlyList<ItemSearchResult>> SearchAsync(int branchId, string term, DateOnly today, CancellationToken ct) =>
        QueryAsync<ItemSearchResult>("dbo.usp_Item_Search", Scoped(new { BranchId = branchId, Term = term, Today = today }), ct);

    #endregion
}

#endregion

#region Stock

/// <summary>REPOSITORY for batches and the stock card.</summary>
internal sealed class StockRepository(IDbSession session, ICurrentUser currentUser)
    : StoredProcedureRepository(session, currentUser), IStockRepository
{
    #region Commands

    public Task<bool> ApplyMovementsAsync(int branchId, IEnumerable<StockMovement> movements, CancellationToken ct)
    {
        var rows = movements
            .Select(m => new { m.BatchId, m.Quantity, Type = (byte)m.Type, m.DocumentType, m.DocumentId, m.Reason })
            .ToList();

        // Nothing to move (for example a return of non-resaleable items only) is a success.
        return rows.Count == 0
            ? Task.FromResult(true)
            : ExecuteScalarAsync<bool>("dbo.usp_Stock_ApplyMovements",
                ScopedWithUser(new { BranchId = branchId, MovementsJson = Json(rows) }), ct);
    }

    public Task<int> ReceiveBatchAsync(int branchId, int itemId, string batchNumber, DateOnly expiryDate, int quantity,
        decimal unitCost, int goodsReceiptId, CancellationToken ct) =>
        ExecuteScalarAsync<int>("dbo.usp_Batch_Receive", ScopedWithUser(new
        {
            BranchId = branchId, ItemId = itemId, BatchNumber = batchNumber, ExpiryDate = expiryDate,
            Quantity = quantity, UnitCost = unitCost, GoodsReceiptId = goodsReceiptId
        }), ct);

    public Task<int> RecallBatchAsync(int itemId, string batchNumber, CancellationToken ct) =>
        ExecuteScalarAsync<int>("dbo.usp_Batch_Recall", Scoped(new { ItemId = itemId, BatchNumber = batchNumber }), ct);

    public Task<int> QuarantineExpiredAsync(DateOnly today, CancellationToken ct) =>
        ExecuteScalarAsync<int>("dbo.usp_Job_Batch_QuarantineExpired", new { Today = today }, ct);

    #endregion

    #region Queries

    public Task<IReadOnlyList<Batch>> GetBatchesAsync(int branchId, IEnumerable<int> itemIds, CancellationToken ct) =>
        QueryAsync<Batch>("dbo.usp_Batch_GetByItems", Scoped(new { BranchId = branchId, ItemIdsJson = Json(itemIds) }), ct);

    public Task<Batch?> GetBatchAsync(int batchId, CancellationToken ct) =>
        QuerySingleOrDefaultAsync<Batch>("dbo.usp_Batch_GetById", Scoped(new { BatchId = batchId }), ct);

    public Task<IReadOnlyList<StockCardLine>> GetStockCardAsync(int branchId, int itemId, CancellationToken ct) =>
        QueryAsync<StockCardLine>("dbo.usp_Stock_GetCard", Scoped(new { BranchId = branchId, ItemId = itemId }), ct);

    public Task<IReadOnlyList<ExpiryAlert>> GetNearExpiryAsync(int branchId, DateOnly until, CancellationToken ct) =>
        QueryAsync<ExpiryAlert>("dbo.usp_Stock_GetNearExpiry", Scoped(new { BranchId = branchId, Until = until }), ct);

    public Task<IReadOnlyList<LowStockAlert>> GetLowStockAsync(int branchId, DateOnly today, CancellationToken ct) =>
        QueryAsync<LowStockAlert>("dbo.usp_Stock_GetLowStock", Scoped(new { BranchId = branchId, Today = today }), ct);

    #endregion
}

#endregion
