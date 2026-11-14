using RxSuite.Application.Abstractions;
using RxSuite.Application.Features.Purchasing;
using RxSuite.Domain.Purchasing;
using RxSuite.Infrastructure.Persistence.Connection;

namespace RxSuite.Infrastructure.Persistence.Repositories;

#region Suppliers

/// <summary>REPOSITORY for suppliers.</summary>
internal sealed class SupplierRepository(IDbSession session, ICurrentUser currentUser)
    : StoredProcedureRepository(session, currentUser), ISupplierRepository
{
    public async Task<int> AddAsync(Supplier s, CancellationToken ct)
    {
        var id = await ExecuteScalarAsync<int>("dbo.usp_Supplier_Insert", Scoped(new
        {
            s.NameEn, s.NameAr, Type = (byte)s.Type, s.TaxNumber, s.Phone, s.PaymentTermDays, s.CreditLimit
        }), ct);

        s.AssignId(id);
        return id;
    }

    public Task<Supplier?> GetByIdAsync(int supplierId, CancellationToken ct) =>
        QuerySingleOrDefaultAsync<Supplier>("dbo.usp_Supplier_GetById", Scoped(new { SupplierId = supplierId }), ct);

    public Task<IReadOnlyList<SupplierDto>> GetAllAsync(CancellationToken ct) =>
        QueryAsync<SupplierDto>("dbo.usp_Supplier_GetAll", Scoped(), ct);

    public Task IncreaseBalanceAsync(int supplierId, decimal amount, CancellationToken ct) =>
        ExecuteAsync("dbo.usp_Supplier_IncreaseBalance", Scoped(new { SupplierId = supplierId, Amount = amount }), ct);
}

#endregion

#region Purchase orders

/// <summary>REPOSITORY for purchase orders. The lines travel as JSON in the same call as the header.</summary>
internal sealed class PurchaseOrderRepository(IDbSession session, ICurrentUser currentUser)
    : StoredProcedureRepository(session, currentUser), IPurchaseOrderRepository
{
    public async Task<int> AddAsync(PurchaseOrder o, CancellationToken ct)
    {
        var id = await ExecuteScalarAsync<int>("dbo.usp_PurchaseOrder_Insert", Scoped(new
        {
            o.SupplierId, o.BranchId, Status = (byte)o.Status, o.ExpectedDeliveryDate, o.Notes, o.Total, o.CreatedBy,
            LinesJson = Json(o.Lines.Select(l => new { l.ItemId, l.QuantityBoxes, l.BonusBoxes, l.UnitCost, l.DiscountPercent }))
        }), ct);

        o.AssignId(id);
        return id;
    }

    /// <summary>Result set 1: the order. Result set 2: its lines.</summary>
    public Task<PurchaseOrder?> GetByIdAsync(int orderId, CancellationToken ct) =>
        QueryMultipleAsync("dbo.usp_PurchaseOrder_GetById", Scoped(new { PurchaseOrderId = orderId }), async grid =>
        {
            var order = await grid.ReadSingleOrDefaultAsync<PurchaseOrder>();
            var lines = await grid.ReadAsync<PurchaseOrderLine>();

            order?.LoadLines(lines);
            return order;
        }, ct);

    public Task UpdateAsync(PurchaseOrder o, CancellationToken ct) =>
        ExecuteAsync("dbo.usp_PurchaseOrder_Update", Scoped(new
        {
            PurchaseOrderId = o.Id, Status = (byte)o.Status, o.ApprovedBy,
            LinesJson = Json(o.Lines.Select(l => new { l.ItemId, l.ReceivedBoxes }))
        }), ct);
}

#endregion

#region Goods receipts

/// <summary>REPOSITORY for goods receipts.</summary>
internal sealed class GoodsReceiptRepository(IDbSession session, ICurrentUser currentUser)
    : StoredProcedureRepository(session, currentUser), IGoodsReceiptRepository
{
    public async Task<int> AddAsync(GoodsReceipt r, CancellationToken ct)
    {
        var id = await ExecuteScalarAsync<int>("dbo.usp_GoodsReceipt_Insert", Scoped(new
        {
            r.SupplierId, r.BranchId, r.PurchaseOrderId, r.SupplierInvoiceNumber, r.ReceivedBy, r.Total,
            LinesJson = Json(r.Lines.Select(l => new
            {
                l.ItemId, l.BatchNumber, l.ExpiryDate, l.QuantityBoxes, l.BonusBoxes, l.CostPerBox,
                l.DiscountPercent, l.VatRate, l.ShelfLifeOverrideReason
            }))
        }), ct);

        r.AssignId(id);
        return id;
    }
}

#endregion
