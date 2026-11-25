using RxSuite.Application.Abstractions;
using RxSuite.Application.Features.Transfers;
using RxSuite.Domain.Catalog;
using RxSuite.Domain.Transfers;
using RxSuite.Infrastructure.Persistence.Connection;

namespace RxSuite.Infrastructure.Persistence.Repositories;

#region Transfers

/// <summary>
/// REPOSITORY for stock transfers. A transfer and its lines are saved by one call; the shipped
/// batches are added at shipping and completed at receipt, both through usp_Transfer_Update.
/// </summary>
internal sealed class TransferRepository(IDbSession session, ICurrentUser currentUser)
    : StoredProcedureRepository(session, currentUser), ITransferRepository
{
    #region Commands

    public async Task<int> AddAsync(Transfer t, CancellationToken ct)
    {
        var id = await ExecuteScalarAsync<int>("dbo.usp_Transfer_Insert", Scoped(new
        {
            t.FromBranchId, t.ToBranchId, Status = (byte)t.Status, t.IsDirect, t.Note, t.RequestedBy, t.RequestedAtUtc,
            t.ApprovedBy, t.ApprovedAtUtc,
            LinesJson = Json(t.Lines.Select(l => new { l.LineNumber, l.ItemId, l.ItemName, l.RequestedQuantity }))
        }), ct);

        t.AssignId(id);
        return id;
    }

    public Task UpdateAsync(Transfer t, CancellationToken ct) =>
        ExecuteAsync("dbo.usp_Transfer_Update", Scoped(new
        {
            TransferId = t.Id, Status = (byte)t.Status, t.ApprovedBy, t.ApprovedAtUtc, t.ShippedBy, t.ShippedAtUtc,
            t.ReceivedBy, t.ReceivedAtUtc, t.ClosingNote,
            BatchesJson = Json(t.Batches.Select(b => new
            {
                b.LineNumber, b.SourceBatchId, b.BatchNumber, b.ExpiryDate, b.UnitCost, b.QuantitySent, b.QuantityReceived,
                b.DestinationBatchId
            }))
        }), ct);

    #endregion

    #region Queries

    /// <summary>Result sets: 1 the transfer, 2 its lines, 3 the shipped batches.</summary>
    public Task<Transfer?> GetByIdAsync(int transferId, CancellationToken ct) =>
        QueryMultipleAsync("dbo.usp_Transfer_GetById", Scoped(new { TransferId = transferId }), async grid =>
        {
            var transfer = await grid.ReadSingleOrDefaultAsync<Transfer>();
            var lines = await grid.ReadAsync<TransferLine>();
            var batches = await grid.ReadAsync<TransferBatch>();
            transfer?.Load(lines, batches);
            return transfer;
        }, ct);

    public Task<IReadOnlyList<TransferSummaryDto>> GetListAsync(int? branchId, TransferStatus? status, CancellationToken ct) =>
        QueryAsync<TransferSummaryDto>("dbo.usp_Transfer_GetList",
            Scoped(new { BranchId = branchId, Status = (byte?)status }), ct);

    #endregion
}

#endregion

#region Branch item settings and HQ reports

/// <summary>REPOSITORY for per-branch price, min/max and visibility (MBR-02).</summary>
internal sealed class BranchItemSettingRepository(IDbSession session, ICurrentUser currentUser)
    : StoredProcedureRepository(session, currentUser), IBranchItemSettingRepository
{
    public Task SaveAsync(BranchItemSetting s, CancellationToken ct) =>
        ExecuteAsync("dbo.usp_BranchItemSetting_Save",
            Scoped(new { s.BranchId, s.ItemId, s.SellingPrice, s.MinStock, s.MaxStock, s.IsHidden }), ct);

    public Task<IReadOnlyList<BranchItemSetting>> GetAsync(int branchId, IEnumerable<int> itemIds, CancellationToken ct) =>
        QueryAsync<BranchItemSetting>("dbo.usp_BranchItemSetting_GetByItems",
            Scoped(new { BranchId = branchId, ItemIdsJson = Json(itemIds) }), ct);
}

/// <summary>Cross-branch read models (MBR-05, MBR-06, MBR-08), straight from their procedures.</summary>
internal sealed class BranchReportRepository(IDbSession session, ICurrentUser currentUser)
    : StoredProcedureRepository(session, currentUser), IBranchReportRepository
{
    public Task<(IReadOnlyList<StockMatrixRow> Batches, IReadOnlyList<InTransitRow> InTransit)> GetStockMatrixAsync(string term,
        CancellationToken ct) =>
        QueryMultipleAsync("dbo.usp_Report_StockMatrix", Scoped(new { Term = term }), async grid =>
        {
            IReadOnlyList<StockMatrixRow> batches = (await grid.ReadAsync<StockMatrixRow>()).ToList();
            IReadOnlyList<InTransitRow> inTransit = (await grid.ReadAsync<InTransitRow>()).ToList();
            return (batches, inTransit);
        }, ct);

    public Task<IReadOnlyList<RebalancingSuggestionDto>> GetRebalancingSuggestionsAsync(DateOnly today, DateOnly nearExpiryUntil,
        int salesWindowDays, CancellationToken ct) =>
        QueryAsync<RebalancingSuggestionDto>("dbo.usp_Report_RebalancingSuggestions",
            Scoped(new { Today = today, NearExpiryUntil = nearExpiryUntil, SalesWindowDays = salesWindowDays }), ct);

    public Task<IReadOnlyList<BranchPerformanceDto>> GetBranchPerformanceAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        QueryAsync<BranchPerformanceDto>("dbo.usp_Report_BranchPerformance", Scoped(new { From = from, To = to }), ct);
}

#endregion
