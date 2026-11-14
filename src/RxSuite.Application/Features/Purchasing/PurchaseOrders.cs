using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Domain.Common;
using RxSuite.Domain.Identity;
using RxSuite.Domain.Purchasing;
using RxSuite.Domain.Rules;

namespace RxSuite.Application.Features.Purchasing;

// USE CASES: purchase orders and their approval (PUR-03, PUR-04, AC-PUR-1, BR-PUR-4; Figure 6).

#region DTOs

public sealed record PurchaseOrderLineDto(int ItemId, int QuantityBoxes, int BonusBoxes, decimal UnitCost,
    decimal DiscountPercent, int ReceivedBoxes, decimal LineTotal);

public sealed record PurchaseOrderDto(int Id, int SupplierId, int BranchId, PurchaseOrderStatus Status,
    DateOnly? ExpectedDeliveryDate, string Notes, decimal Total, int CreatedBy, int? ApprovedBy,
    IReadOnlyList<PurchaseOrderLineDto> Lines);

/// <summary>The new order plus the BR-PUR-4 warning, when the supplier goes over its credit limit.</summary>
public sealed record PurchaseOrderCreated(int Id, PurchaseOrderStatus Status, decimal Total, string? Warning);

#endregion

#region Command: create and submit an order

public sealed record PurchaseOrderLineRequest(int ItemId, int QuantityBoxes, int BonusBoxes, decimal UnitCost, decimal DiscountPercent);

[RequiresPermission(Permissions.PurchasingCreate)]
public sealed record CreatePurchaseOrderCommand(int SupplierId, int BranchId, DateOnly? ExpectedDeliveryDate,
    string? Notes, IReadOnlyList<PurchaseOrderLineRequest> Lines) : ICommand<PurchaseOrderCreated>;

internal sealed class CreatePurchaseOrderHandler(
    IPurchaseOrderRepository orders, ISupplierRepository suppliers, IItemRepository items,
    ICurrentUser currentUser, BusinessRules rules) : IRequestHandler<CreatePurchaseOrderCommand, PurchaseOrderCreated>
{
    public async Task<Result<PurchaseOrderCreated>> HandleAsync(CreatePurchaseOrderCommand c, CancellationToken ct)
    {
        if (!currentUser.CanAccessBranch(c.BranchId)) return Errors.BranchNotAllowed;
        if (c.Lines is null || c.Lines.Count == 0)
            return Error.Validation("PurchaseOrder.Empty", "A purchase order needs at least one line.");

        var supplier = await suppliers.GetByIdAsync(c.SupplierId, ct);
        if (supplier is not { IsActive: true }) return Errors.NotFound("Supplier", c.SupplierId);

        var itemIds = c.Lines.Select(l => l.ItemId).Distinct().ToList();
        var known = (await items.GetByIdsAsync(itemIds, ct)).Select(i => i.Id).ToHashSet();
        var missing = itemIds.Where(id => !known.Contains(id)).ToList();
        if (missing.Count > 0) return Errors.NotFound("Item", missing[0]);

        var order = PurchaseOrder.Create(currentUser.TenantId, c.SupplierId, c.BranchId, currentUser.UserId,
            c.ExpectedDeliveryDate, c.Notes,
            c.Lines.Select(l => PurchaseOrderLine.Create(l.ItemId, l.QuantityBoxes, l.BonusBoxes, l.UnitCost, l.DiscountPercent)));

        order.Submit(rules.Purchasing.OrderApprovalLimit);   // AC-PUR-1
        var id = await orders.AddAsync(order, ct);

        var warning = supplier.WouldExceedCreditLimit(order.Total)
            ? $"This order takes {supplier.NameEn} over its credit limit of {supplier.CreditLimit:0.00} EGP."
            : null;

        return new PurchaseOrderCreated(id, order.Status, order.Total, warning);
    }
}

#endregion

#region Command: approve an order

[RequiresPermission(Permissions.PurchasingApprove)]
public sealed record ApprovePurchaseOrderCommand(int PurchaseOrderId) : ICommand<Unit>;

internal sealed class ApprovePurchaseOrderHandler(IPurchaseOrderRepository orders, ICurrentUser currentUser, IAuditTrail audit)
    : IRequestHandler<ApprovePurchaseOrderCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(ApprovePurchaseOrderCommand c, CancellationToken ct)
    {
        var order = await orders.GetByIdAsync(c.PurchaseOrderId, ct);
        if (order is null) return Errors.NotFound("PurchaseOrder", c.PurchaseOrderId);
        if (!currentUser.CanAccessBranch(order.BranchId)) return Errors.BranchNotAllowed;

        order.Approve(currentUser.UserId);
        await orders.UpdateAsync(order, ct);
        await audit.RecordAsync("Purchasing", "OrderApproved", order.Id, newValue: order.Total.ToString("0.00"),
            branchId: order.BranchId, ct: ct);
        return Unit.Value;
    }
}

#endregion

#region Query: one order

[RequiresPermission(Permissions.PurchasingCreate)]
public sealed record GetPurchaseOrderQuery(int PurchaseOrderId) : IQuery<PurchaseOrderDto>;

internal sealed class GetPurchaseOrderHandler(IPurchaseOrderRepository orders, ICurrentUser currentUser)
    : IRequestHandler<GetPurchaseOrderQuery, PurchaseOrderDto>
{
    public async Task<Result<PurchaseOrderDto>> HandleAsync(GetPurchaseOrderQuery q, CancellationToken ct)
    {
        var order = await orders.GetByIdAsync(q.PurchaseOrderId, ct);
        if (order is null) return Errors.NotFound("PurchaseOrder", q.PurchaseOrderId);
        if (!currentUser.CanAccessBranch(order.BranchId)) return Errors.BranchNotAllowed;

        return new PurchaseOrderDto(order.Id, order.SupplierId, order.BranchId, order.Status,
            order.ExpectedDeliveryDate, order.Notes, order.Total, order.CreatedBy, order.ApprovedBy,
            order.Lines.Select(l => new PurchaseOrderLineDto(l.ItemId, l.QuantityBoxes, l.BonusBoxes, l.UnitCost,
                l.DiscountPercent, l.ReceivedBoxes, l.LineTotal)).ToList());
    }
}

#endregion
