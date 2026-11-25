using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Domain.Common;
using RxSuite.Domain.Controlled;
using RxSuite.Domain.Identity;
using RxSuite.Domain.Transfers;

namespace RxSuite.Application.Features.Transfers;

// USE CASES (BRD 8.8): stock transfers between branches and warehouses.
// MBR-03 request -> approve -> ship -> receive with shortages (AC-MBR-1); MBR-04 direct transfers;
// BR-MBR-1 in transit is nobody's sellable stock; BR-MBR-2 batch and expiry are kept;
// BR-MBR-3 controlled items get register entries at both ends, by a pharmacist at each end.

#region Read models

public sealed class TransferSummaryDto
{
    public int Id { get; set; }
    public int FromBranchId { get; set; }
    public string FromBranchName { get; set; } = string.Empty;
    public int ToBranchId { get; set; }
    public string ToBranchName { get; set; } = string.Empty;
    public TransferStatus Status { get; set; }
    public bool IsDirect { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? ShippedAtUtc { get; set; }
    public DateTime? ReceivedAtUtc { get; set; }
    public int LineCount { get; set; }

    /// <summary>AC-MBR-1: units sent that never arrived.</summary>
    public int TotalShortage { get; set; }
}

public sealed record TransferLineDto(int LineNumber, int ItemId, string ItemName, int RequestedQuantity, int ShippedQuantity,
    int? ReceivedQuantity);

public sealed record TransferBatchDto(int LineNumber, int SourceBatchId, string BatchNumber, DateOnly ExpiryDate,
    int QuantitySent, int? QuantityReceived, int Shortage);

public sealed record TransferDto(int Id, int FromBranchId, int ToBranchId, TransferStatus Status, bool IsDirect, string? Note,
    DateTime RequestedAtUtc, DateTime? ApprovedAtUtc, DateTime? ShippedAtUtc, DateTime? ReceivedAtUtc, string? ClosingNote,
    IReadOnlyList<TransferLineDto> Lines, IReadOnlyList<TransferBatchDto> Batches);

internal static class TransferMappings
{
    public static TransferDto ToDto(this Transfer t) => new(t.Id, t.FromBranchId, t.ToBranchId, t.Status, t.IsDirect, t.Note,
        t.RequestedAtUtc, t.ApprovedAtUtc, t.ShippedAtUtc, t.ReceivedAtUtc, t.ClosingNote,
        t.Lines.Select(l =>
        {
            var batches = t.Batches.Where(b => b.LineNumber == l.LineNumber).ToList();
            return new TransferLineDto(l.LineNumber, l.ItemId, l.ItemName, l.RequestedQuantity, batches.Sum(b => b.QuantitySent),
                batches.Count == 0 || batches.Any(b => b.QuantityReceived is null) ? null : batches.Sum(b => b.QuantityReceived!.Value));
        }).ToList(),
        t.Batches.Select(b => new TransferBatchDto(b.LineNumber, b.SourceBatchId, b.BatchNumber, b.ExpiryDate, b.QuantitySent,
            b.QuantityReceived, b.QuantityReceived is null ? 0 : b.Shortage)).ToList());
}

#endregion

#region Shared checks

/// <summary>Both locations must belong to the tenant.</summary>
internal static class TransferChecks
{
    public static async Task<Error?> LocationsExistAsync(IPlatformRepository platform, ICurrentUser user, int from, int to,
        CancellationToken ct)
    {
        var branches = (await platform.GetBranchesAsync(user.TenantId, ct)).Select(b => b.Id).ToHashSet();
        if (!branches.Contains(from)) return Errors.NotFound("Branch", from);
        if (!branches.Contains(to)) return Errors.NotFound("Branch", to);
        return null;
    }
}

#endregion

#region Commands: request, or transfer directly (US-MBR-2, MBR-03, MBR-04)

/// <summary>US-MBR-2: a branch asks another location (usually the warehouse) for stock.</summary>
[RequiresPermission(Permissions.TransfersRequest)]
public sealed record RequestTransferCommand(int FromBranchId, int ToBranchId, IReadOnlyList<TransferLineRequest> Lines, string? Note)
    : ICommand<int>;

/// <summary>MBR-04: HQ or the warehouse sends stock without a request; it is approved at once.</summary>
[RequiresPermission(Permissions.TransfersApprove)]
public sealed record CreateDirectTransferCommand(int FromBranchId, int ToBranchId, IReadOnlyList<TransferLineRequest> Lines, string? Note)
    : ICommand<int>;

internal sealed class RequestTransferHandler(ITransferRepository transfers, IItemRepository items, IPlatformRepository platform,
    ICurrentUser currentUser, IClock clock, IAuditTrail audit)
    : IRequestHandler<RequestTransferCommand, int>, IRequestHandler<CreateDirectTransferCommand, int>
{
    public Task<Result<int>> HandleAsync(RequestTransferCommand c, CancellationToken ct) =>
        // The requester asks for stock INTO a branch they work at.
        currentUser.CanAccessBranch(c.ToBranchId)
            ? CreateAsync(c.FromBranchId, c.ToBranchId, c.Lines, c.Note, isDirect: false, ct)
            : Task.FromResult<Result<int>>(Errors.BranchNotAllowed);

    public Task<Result<int>> HandleAsync(CreateDirectTransferCommand c, CancellationToken ct) =>
        currentUser.CanAccessBranch(c.FromBranchId)
            ? CreateAsync(c.FromBranchId, c.ToBranchId, c.Lines, c.Note, isDirect: true, ct)
            : Task.FromResult<Result<int>>(Errors.BranchNotAllowed);

    private async Task<Result<int>> CreateAsync(int from, int to, IReadOnlyList<TransferLineRequest>? lines, string? note,
        bool isDirect, CancellationToken ct)
    {
        if (lines is null || lines.Count == 0) return Error.Validation("Transfer.Empty", "A transfer needs at least one item.");
        if (await TransferChecks.LocationsExistAsync(platform, currentUser, from, to, ct) is { } missing) return missing;

        var itemsById = (await items.GetByIdsAsync(lines.Select(l => l.ItemId).Distinct(), ct)).ToDictionary(i => i.Id);
        if (lines.FirstOrDefault(l => !itemsById.ContainsKey(l.ItemId)) is { } unknown) return Errors.NotFound("Item", unknown.ItemId);

        var transfer = Transfer.Create(currentUser.TenantId, from, to, lines, itemsById, isDirect, note, currentUser.UserId, clock.UtcNow);
        var id = await transfers.AddAsync(transfer, ct);

        await audit.RecordAsync("Transfers", isDirect ? "DirectTransferCreated" : "TransferRequested", id,
            newValue: $"{from} -> {to}", branchId: to, ct: ct);
        return id;
    }
}

#endregion

#region Commands: approve, reject, cancel

[RequiresPermission(Permissions.TransfersApprove)]
public sealed record ApproveTransferCommand(int TransferId) : ICommand<Unit>;

[RequiresPermission(Permissions.TransfersApprove)]
public sealed record RejectTransferCommand(int TransferId, string Reason) : ICommand<Unit>;

/// <summary>Withdraws a request or an approved transfer before anything is shipped.</summary>
[RequiresPermission(Permissions.TransfersRequest)]
public sealed record CancelTransferCommand(int TransferId, string Reason) : ICommand<Unit>;

internal sealed class TransferDecisionHandler(ITransferRepository transfers, ICurrentUser currentUser, IClock clock, IAuditTrail audit)
    : IRequestHandler<ApproveTransferCommand, Unit>, IRequestHandler<RejectTransferCommand, Unit>,
      IRequestHandler<CancelTransferCommand, Unit>
{
    public Task<Result<Unit>> HandleAsync(ApproveTransferCommand c, CancellationToken ct) =>
        DecideAsync(c.TransferId, "TransferApproved", null, t => t.Approve(currentUser.UserId, clock.UtcNow), ct);

    public Task<Result<Unit>> HandleAsync(RejectTransferCommand c, CancellationToken ct) =>
        DecideAsync(c.TransferId, "TransferRejected", c.Reason, t => t.Reject(c.Reason, currentUser.UserId, clock.UtcNow), ct);

    public Task<Result<Unit>> HandleAsync(CancelTransferCommand c, CancellationToken ct) =>
        DecideAsync(c.TransferId, "TransferCancelled", c.Reason, t => t.Cancel(c.Reason), ct);

    private async Task<Result<Unit>> DecideAsync(int transferId, string action, string? reason, Action<Transfer> decide,
        CancellationToken ct)
    {
        var transfer = await transfers.GetByIdAsync(transferId, ct);
        if (transfer is null) return Errors.NotFound("Transfer", transferId);
        if (!currentUser.CanAccessBranch(transfer.FromBranchId) && !currentUser.CanAccessBranch(transfer.ToBranchId))
            return Errors.BranchNotAllowed;

        decide(transfer);
        await transfers.UpdateAsync(transfer, ct);
        await audit.RecordAsync("Transfers", action, transfer.Id, reason: reason?.Trim(), branchId: transfer.ToBranchId, ct: ct);
        return Unit.Value;
    }
}

#endregion

#region Command: pick and ship (MBR-03, BR-MBR-1/2/3)

[RequiresPermission(Permissions.TransfersShip)]
public sealed record ShipTransferCommand(int TransferId) : ICommand<TransferDto>;

internal sealed class ShipTransferHandler(ITransferRepository transfers, IItemRepository items, IStockRepository stock,
    IControlledRegisterRepository register, IPharmacistCheck pharmacists, ICurrentUser currentUser, IClock clock, IAuditTrail audit)
    : IRequestHandler<ShipTransferCommand, TransferDto>
{
    public async Task<Result<TransferDto>> HandleAsync(ShipTransferCommand c, CancellationToken ct)
    {
        var transfer = await transfers.GetByIdAsync(c.TransferId, ct);
        if (transfer is null) return Errors.NotFound("Transfer", c.TransferId);
        if (!currentUser.CanAccessBranch(transfer.FromBranchId)) return Errors.BranchNotAllowed;

        var itemIds = transfer.Lines.Select(l => l.ItemId).ToList();
        var controlled = (await items.GetByIdsAsync(itemIds, ct)).Where(i => i.IsControlled).Select(i => i.Id).ToHashSet();

        // CTL-02: controlled stock leaves only through a pharmacist.
        int? pharmacistId = null;
        if (controlled.Count > 0)
        {
            var pharmacist = await pharmacists.CurrentAsync(ct);
            if (pharmacist.IsFailure) return pharmacist.Error;
            pharmacistId = pharmacist.Value.Id;
        }

        transfer.Ship(await stock.GetBatchesAsync(transfer.FromBranchId, itemIds, ct), clock.Today, currentUser.UserId, clock.UtcNow);

        // BR-MBR-1: out of the source now; it reaches the destination only when received.
        if (!await stock.ApplyMovementsAsync(transfer.FromBranchId, transfer.ShipMovements(), ct)) return Errors.StockChanged;
        await transfers.UpdateAsync(transfer, ct);

        // BR-MBR-3: register entry at the source.
        var entries = transfer.Batches
            .Select(b => (Batch: b, ItemId: transfer.Lines.First(l => l.LineNumber == b.LineNumber).ItemId))
            .Where(x => controlled.Contains(x.ItemId))
            .Select(x => ControlledRegisterEntry.ForStockMovement(currentUser.TenantId, transfer.FromBranchId, x.ItemId,
                x.Batch.SourceBatchId, RegisterEntryType.TransferOut, -x.Batch.QuantitySent, "Transfer", transfer.Id,
                pharmacistId!.Value, $"To branch {transfer.ToBranchId}", clock.UtcNow))
            .ToList();
        if (entries.Count > 0 && !await register.AddEntriesAsync(entries, ct)) return Errors.RegisterNegative;

        await audit.RecordAsync("Transfers", "TransferShipped", transfer.Id, newValue: $"{transfer.Batches.Sum(b => b.QuantitySent)} unit(s)",
            branchId: transfer.FromBranchId, ct: ct);
        return transfer.ToDto();
    }
}

#endregion

#region Command: receive, with shortages (MBR-03, AC-MBR-1)

[RequiresPermission(Permissions.TransfersReceive)]
public sealed record ReceiveTransferCommand(int TransferId, IReadOnlyList<TransferReceiptLine> Lines, string? Note)
    : ICommand<TransferDto>;

internal sealed class ReceiveTransferHandler(ITransferRepository transfers, IItemRepository items, IStockRepository stock,
    IControlledRegisterRepository register, IPharmacistCheck pharmacists, ICurrentUser currentUser, IClock clock, IAuditTrail audit)
    : IRequestHandler<ReceiveTransferCommand, TransferDto>
{
    public async Task<Result<TransferDto>> HandleAsync(ReceiveTransferCommand c, CancellationToken ct)
    {
        var transfer = await transfers.GetByIdAsync(c.TransferId, ct);
        if (transfer is null) return Errors.NotFound("Transfer", c.TransferId);
        if (!currentUser.CanAccessBranch(transfer.ToBranchId)) return Errors.BranchNotAllowed;

        var itemsById = (await items.GetByIdsAsync(transfer.Lines.Select(l => l.ItemId), ct)).ToDictionary(i => i.Id);
        int? pharmacistId = null;
        if (itemsById.Values.Any(i => i.IsControlled))
        {
            var pharmacist = await pharmacists.CurrentAsync(ct);
            if (pharmacist.IsFailure) return pharmacist.Error;
            pharmacistId = pharmacist.Value.Id;
        }

        transfer.Receive(c.Lines ?? [], c.Note, currentUser.UserId, clock.UtcNow);

        var entries = new List<ControlledRegisterEntry>();
        foreach (var batch in transfer.Batches.Where(b => b.QuantityReceived > 0))
        {
            var itemId = transfer.Lines.First(l => l.LineNumber == batch.LineNumber).ItemId;

            // BR-MBR-2: the same batch number, expiry and cost at the destination.
            var destinationBatchId = await stock.ReceiveTransferBatchAsync(transfer.ToBranchId, itemId, batch.BatchNumber,
                batch.ExpiryDate, batch.QuantityReceived!.Value, batch.UnitCost, transfer.Id, ct);
            batch.AttachDestinationBatch(destinationBatchId);

            if (itemsById[itemId].IsControlled)   // BR-MBR-3: register entry at the destination
                entries.Add(ControlledRegisterEntry.ForStockMovement(currentUser.TenantId, transfer.ToBranchId, itemId,
                    destinationBatchId, RegisterEntryType.TransferIn, batch.QuantityReceived.Value, "Transfer", transfer.Id,
                    pharmacistId!.Value, $"From branch {transfer.FromBranchId}", clock.UtcNow));
        }

        if (entries.Count > 0 && !await register.AddEntriesAsync(entries, ct)) return Errors.RegisterNegative;
        await transfers.UpdateAsync(transfer, ct);

        await audit.RecordAsync("Transfers", "TransferReceived", transfer.Id, branchId: transfer.ToBranchId, ct: ct);

        // AC-MBR-1: the source and HQ are told about the shortage. Both see it in the audit trail and in
        // the transfers list filtered on ReceivedWithShortage.
        if (transfer.TotalShortage > 0)
            await audit.RecordAsync("Transfers", "TransferShortage", transfer.Id,
                oldValue: $"sent {transfer.Batches.Sum(b => b.QuantitySent)}",
                newValue: $"received {transfer.Batches.Sum(b => b.QuantityReceived ?? 0)}",
                reason: transfer.ClosingNote, branchId: transfer.FromBranchId, ct: ct);

        return transfer.ToDto();
    }
}

#endregion

#region Queries

[RequiresPermission(Permissions.InventoryView)]
public sealed record GetTransferQuery(int TransferId) : IQuery<TransferDto>;

internal sealed class GetTransferHandler(ITransferRepository transfers, ICurrentUser currentUser) : IRequestHandler<GetTransferQuery, TransferDto>
{
    public async Task<Result<TransferDto>> HandleAsync(GetTransferQuery q, CancellationToken ct)
    {
        var transfer = await transfers.GetByIdAsync(q.TransferId, ct);
        if (transfer is null) return Errors.NotFound("Transfer", q.TransferId);
        return currentUser.CanAccessBranch(transfer.FromBranchId) || currentUser.CanAccessBranch(transfer.ToBranchId)
            ? transfer.ToDto()
            : Errors.BranchNotAllowed;
    }
}

/// <summary>Transfer requests, transfers in transit and shortages: filter by branch and status.</summary>
[RequiresPermission(Permissions.InventoryView)]
public sealed record GetTransfersQuery(int? BranchId, TransferStatus? Status) : IQuery<IReadOnlyList<TransferSummaryDto>>;

internal sealed class GetTransfersHandler(ITransferRepository transfers, ICurrentUser currentUser)
    : IRequestHandler<GetTransfersQuery, IReadOnlyList<TransferSummaryDto>>
{
    public async Task<Result<IReadOnlyList<TransferSummaryDto>>> HandleAsync(GetTransfersQuery q, CancellationToken ct)
    {
        if (q.BranchId is { } branchId && !currentUser.CanAccessBranch(branchId)) return Errors.BranchNotAllowed;

        // Only transfers touching a branch the user works at.
        return (await transfers.GetListAsync(q.BranchId, q.Status, ct))
            .Where(t => currentUser.CanAccessBranch(t.FromBranchId) || currentUser.CanAccessBranch(t.ToBranchId))
            .ToList();
    }
}

#endregion
