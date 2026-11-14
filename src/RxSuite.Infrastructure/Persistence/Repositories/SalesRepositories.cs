using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Application.Features.Tax;
using RxSuite.Domain.Sales;
using RxSuite.Domain.Tax;
using RxSuite.Infrastructure.Persistence.Connection;

namespace RxSuite.Infrastructure.Persistence.Repositories;

#region Shifts

/// <summary>REPOSITORY for cash shifts.</summary>
internal sealed class ShiftRepository(IDbSession session, ICurrentUser currentUser)
    : StoredProcedureRepository(session, currentUser), IShiftRepository
{
    /// <summary>Shape of the row usp_Shift_GetCashTotals returns.</summary>
    private sealed class CashTotalsRow
    {
        public decimal CashSales { get; set; }
        public decimal CashRefunds { get; set; }
        public decimal PayIns { get; set; }
        public decimal PayOuts { get; set; }
    }

    #region Commands

    public async Task<int> AddAsync(Shift s, CancellationToken ct)
    {
        var id = await ExecuteScalarAsync<int>("dbo.usp_Shift_Insert",
            Scoped(new { s.BranchId, s.CashierId, s.TerminalCode, s.OpeningFloat, s.OpenedAtUtc }), ct);

        s.AssignId(id);
        return id;
    }

    public Task AddCashMovementAsync(int shiftId, CashMovementType type, decimal amount, string reason, CancellationToken ct) =>
        ExecuteAsync("dbo.usp_CashMovement_Insert",
            ScopedWithUser(new { ShiftId = shiftId, Type = (byte)type, Amount = amount, Reason = reason }), ct);

    public Task CloseAsync(Shift s, CancellationToken ct) =>
        ExecuteAsync("dbo.usp_Shift_Close", Scoped(new
        {
            ShiftId = s.Id, s.ExpectedCash, s.CountedCash, s.Variance, s.ClosingNote, s.ApprovedBy, s.ClosedAtUtc
        }), ct);

    #endregion

    #region Queries

    public Task<Shift?> GetOpenAsync(int branchId, int cashierId, CancellationToken ct) =>
        QuerySingleOrDefaultAsync<Shift>("dbo.usp_Shift_GetOpen", Scoped(new { BranchId = branchId, CashierId = cashierId }), ct);

    public Task<Shift?> GetByIdAsync(int shiftId, CancellationToken ct) =>
        QuerySingleOrDefaultAsync<Shift>("dbo.usp_Shift_GetById", Scoped(new { ShiftId = shiftId }), ct);

    public async Task<ShiftCashTotals> GetCashTotalsAsync(int shiftId, CancellationToken ct)
    {
        var row = await QuerySingleOrDefaultAsync<CashTotalsRow>("dbo.usp_Shift_GetCashTotals", Scoped(new { ShiftId = shiftId }), ct)
                  ?? new CashTotalsRow();

        return new ShiftCashTotals(row.CashSales, row.CashRefunds, row.PayIns, row.PayOuts);
    }

    #endregion
}

#endregion

#region Sales and returns

/// <summary>
/// REPOSITORY for receipts and returns. A whole receipt (header, lines, batch allocations,
/// payments) is saved by ONE procedure call; the children travel as JSON.
/// </summary>
internal sealed class SaleRepository(IDbSession session, ICurrentUser currentUser)
    : StoredProcedureRepository(session, currentUser), ISaleRepository
{
    #region Commands

    public async Task<long> AddAsync(Sale s, CancellationToken ct)
    {
        var id = await ExecuteScalarAsync<long>("dbo.usp_Sale_Insert", Scoped(new
        {
            s.BranchId, s.ShiftId, s.CashierId, s.PharmacistId, s.OfflineId, s.BuyerName, s.BuyerNationalId,
            s.BuyerTaxNumber, s.InvoiceDiscountPercent, s.Total, s.VatTotal, s.ChangeGiven, s.SoldAtUtc,
            LinesJson = Json(s.Lines.Select(l => new
            {
                l.LineNumber, l.ItemId, l.ItemName, UnitType = (byte)l.UnitType, l.Quantity, l.BaseUnitsPerUnit,
                l.UnitPrice, l.DiscountPercent, l.VatRate, l.LineTotal, l.VatAmount
            })),
            AllocationsJson = Json(s.Lines.SelectMany(l => l.Allocations)
                .Select(a => new { a.LineNumber, a.BatchId, a.Quantity })),
            PaymentsJson = Json(s.Payments.Select(p => new { Method = (byte)p.Method, p.Amount, p.Reference }))
        }), ct);

        s.AssignId(id);
        return id;
    }

    public async Task<long> AddReturnAsync(SaleReturn r, CancellationToken ct)
    {
        var id = await ExecuteScalarAsync<long>("dbo.usp_SaleReturn_Insert", Scoped(new
        {
            r.SaleId, r.BranchId, r.ShiftId, r.CashierId, RefundMethod = (byte)r.RefundMethod, r.Reason,
            r.RefundTotal, r.ReturnedAtUtc,
            LinesJson = Json(r.Lines.Select(l => new { l.LineNumber, l.Quantity, l.IsResaleable, l.RefundAmount })),
            RestorationsJson = Json(r.Restorations.Select(x => new { x.LineNumber, x.BatchId, x.Quantity }))
        }), ct);

        r.AssignId(id);
        return id;
    }

    #endregion

    #region Queries

    public Task<long?> GetIdByOfflineIdAsync(Guid offlineId, CancellationToken ct) =>
        QuerySingleOrDefaultAsync<long?>("dbo.usp_Sale_GetIdByOfflineId", Scoped(new { OfflineId = offlineId }), ct);

    /// <summary>Result sets: 1 the receipt, 2 its lines, 3 the batch allocations, 4 the payments.</summary>
    public Task<Sale?> GetByIdAsync(long saleId, CancellationToken ct) =>
        QueryMultipleAsync("dbo.usp_Sale_GetById", Scoped(new { SaleId = saleId }), async grid =>
        {
            var sale = await grid.ReadSingleOrDefaultAsync<Sale>();
            var lines = (await grid.ReadAsync<SaleLine>()).ToList();
            var allocations = (await grid.ReadAsync<SaleLineAllocation>()).ToLookup(a => a.LineNumber);
            var payments = await grid.ReadAsync<SalePayment>();

            foreach (var line in lines) line.LoadAllocations(allocations[line.LineNumber]);
            sale?.Load(lines, payments);
            return sale;
        }, ct);

    #endregion
}

#endregion

#region ETA documents

/// <summary>REPOSITORY for ETA documents and their submission queue.</summary>
internal sealed class EtaDocumentRepository(IDbSession session, ICurrentUser currentUser)
    : StoredProcedureRepository(session, currentUser), IEtaDocumentRepository
{
    #region Commands

    public async Task<long> AddAsync(EtaDocument d, CancellationToken ct)
    {
        var id = await ExecuteScalarAsync<long>("dbo.usp_EtaDocument_Insert", new
        {
            d.TenantId, d.BranchId, Type = (byte)d.Type, d.SaleId, d.SaleReturnId, d.OriginalDocumentId,
            Status = (byte)d.Status, d.CreatedAtUtc
        }, ct);

        d.AssignId(id);
        return id;
    }

    /// <summary>Uses the document's own tenant: the background job has no logged-in user.</summary>
    public Task UpdateAsync(EtaDocument d, CancellationToken ct) =>
        ExecuteAsync("dbo.usp_EtaDocument_Update", new
        {
            d.TenantId, DocumentId = d.Id, Status = (byte)d.Status, d.Uuid, d.LongId, d.QrCode, d.Errors,
            d.Attempts, d.SubmittedAtUtc
        }, ct);

    #endregion

    #region Queries

    public Task<EtaDocument?> GetBySaleIdAsync(long saleId, CancellationToken ct) =>
        QuerySingleOrDefaultAsync<EtaDocument>("dbo.usp_EtaDocument_GetBySale", Scoped(new { SaleId = saleId }), ct);

    public Task<EtaDocument?> GetByIdAsync(long documentId, CancellationToken ct) =>
        QuerySingleOrDefaultAsync<EtaDocument>("dbo.usp_EtaDocument_GetById", Scoped(new { DocumentId = documentId }), ct);

    public Task<IReadOnlyList<EtaDocument>> GetQueuedAsync(int max, CancellationToken ct) =>
        QueryAsync<EtaDocument>("dbo.usp_Job_EtaDocument_GetQueued", new { Max = max }, ct);

    public Task<PagedResult<EtaDocumentDto>> GetPagedAsync(EtaDocumentStatus? status, int pageNumber, int pageSize, CancellationToken ct) =>
        QueryMultipleAsync("dbo.usp_EtaDocument_GetPaged",
            Scoped(new { Status = (byte?)status, PageNumber = pageNumber, PageSize = pageSize }),
            async grid => new PagedResult<EtaDocumentDto>(
                (await grid.ReadAsync<EtaDocumentDto>()).ToList(), await grid.ReadSingleAsync<int>(), pageNumber, pageSize),
            ct);

    #endregion
}

#endregion
