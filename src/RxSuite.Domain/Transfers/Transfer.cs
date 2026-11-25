using RxSuite.Domain.Catalog;
using RxSuite.Domain.Common;
using RxSuite.Domain.Inventory;

namespace RxSuite.Domain.Transfers;

// BRD 8.8 "Multi-branch and warehouse": stock transfers between branches and warehouses.
// MBR-03 request -> approve -> ship (in transit) -> receive, with partial receipt and discrepancies;
// MBR-04 direct transfers by HQ; BR-MBR-1 stock in transit is nobody's sellable stock;
// BR-MBR-2 batch and expiry travel with the stock; AC-MBR-1 shortages are recorded.

#region Enums

public enum TransferStatus : byte
{
    Requested = 1, Approved = 2, InTransit = 3, Received = 4, ReceivedWithShortage = 5, Rejected = 6, Cancelled = 7
}

#endregion

#region Parts

/// <summary>What is asked for: an item and a quantity in a packaging unit.</summary>
public sealed record TransferLineRequest(int ItemId, UnitType Unit, int Quantity);

/// <summary>One requested item. Quantities are in the smallest unit (BR-INV-1).</summary>
public sealed class TransferLine
{
    public int LineNumber { get; private set; }
    public int ItemId { get; private set; }
    public string ItemName { get; private set; } = string.Empty;
    public int RequestedQuantity { get; private set; }

    private TransferLine() { }

    internal static TransferLine Create(int lineNumber, Item item, TransferLineRequest r)
    {
        if (r.Quantity <= 0) throw new DomainException($"Line {lineNumber}: the quantity must be greater than zero.");
        if (!Enum.IsDefined(r.Unit)) throw new DomainException($"Line {lineNumber}: the unit is not valid.");
        return new TransferLine
        {
            LineNumber = lineNumber, ItemId = item.Id, ItemName = item.DisplayName,
            RequestedQuantity = item.ToBaseUnits(r.Quantity, r.Unit)
        };
    }
}

/// <summary>
/// Stock that left the source from one batch. BR-MBR-2: batch number, expiry and cost travel with
/// it, so the destination receives the very same batch.
/// </summary>
public sealed class TransferBatch
{
    public int LineNumber { get; private set; }
    public int SourceBatchId { get; private set; }
    public string BatchNumber { get; private set; } = string.Empty;
    public DateOnly ExpiryDate { get; private set; }
    public decimal UnitCost { get; private set; }
    public int QuantitySent { get; private set; }
    public int? QuantityReceived { get; private set; }

    /// <summary>The batch at the destination, once received.</summary>
    public int? DestinationBatchId { get; private set; }

    private TransferBatch() { }

    internal static TransferBatch Create(int lineNumber, Batch source, int quantity) => new()
    {
        LineNumber = lineNumber, SourceBatchId = source.Id, BatchNumber = source.BatchNumber,
        ExpiryDate = source.ExpiryDate, UnitCost = source.UnitCost, QuantitySent = quantity
    };

    /// <summary>AC-MBR-1: what was sent but did not arrive.</summary>
    public int Shortage => QuantitySent - (QuantityReceived ?? 0);

    internal void Receive(int quantity)
    {
        if (quantity < 0 || quantity > QuantitySent)
            throw new DomainException($"Batch {BatchNumber}: between 0 and {QuantitySent} unit(s) can be received.");
        QuantityReceived = quantity;
    }

    public void AttachDestinationBatch(int batchId) => DestinationBatchId = batchId;
}

/// <summary>What the receiver counted for one shipped batch.</summary>
public sealed record TransferReceiptLine(int SourceBatchId, int QuantityReceived);

#endregion

#region Transfer

/// <summary>
/// AGGREGATE ROOT. Moves stock from one branch or warehouse to another. Owns the status flow:
/// only the arrows below are allowed.
/// </summary>
public sealed class Transfer : TenantEntity<int>
{
    #region State

    private readonly List<TransferLine> _lines = [];
    private readonly List<TransferBatch> _batches = [];

    public int FromBranchId { get; private set; }
    public int ToBranchId { get; private set; }
    public TransferStatus Status { get; private set; }

    /// <summary>MBR-04: made directly by HQ or the warehouse, without a request.</summary>
    public bool IsDirect { get; private set; }
    public string? Note { get; private set; }
    public int RequestedBy { get; private set; }
    public DateTime RequestedAtUtc { get; private set; }
    public int? ApprovedBy { get; private set; }
    public DateTime? ApprovedAtUtc { get; private set; }
    public int? ShippedBy { get; private set; }
    public DateTime? ShippedAtUtc { get; private set; }
    public int? ReceivedBy { get; private set; }
    public DateTime? ReceivedAtUtc { get; private set; }

    /// <summary>Why it was rejected or cancelled, or what the receiver found missing.</summary>
    public string? ClosingNote { get; private set; }
    public IReadOnlyList<TransferLine> Lines => _lines;
    public IReadOnlyList<TransferBatch> Batches => _batches;

    private Transfer() { }

    #endregion

    #region Status flow

    private static readonly Dictionary<TransferStatus, TransferStatus[]> AllowedMoves = new()
    {
        [TransferStatus.Requested] = [TransferStatus.Approved, TransferStatus.Rejected, TransferStatus.Cancelled],
        [TransferStatus.Approved] = [TransferStatus.InTransit, TransferStatus.Cancelled],
        [TransferStatus.InTransit] = [TransferStatus.Received, TransferStatus.ReceivedWithShortage],
        [TransferStatus.Received] = [],
        [TransferStatus.ReceivedWithShortage] = [],
        [TransferStatus.Rejected] = [],
        [TransferStatus.Cancelled] = []
    };

    private void MoveTo(TransferStatus next)
    {
        if (!AllowedMoves[Status].Contains(next))
            throw new DomainException($"A transfer cannot go from {Status} to {next}.");
        Status = next;
    }

    #endregion

    #region Factory

    /// <summary>US-MBR-2 / MBR-03: a request; MBR-04: a direct transfer, approved at once.</summary>
    public static Transfer Create(int tenantId, int fromBranchId, int toBranchId, IReadOnlyList<TransferLineRequest> lines,
        IReadOnlyDictionary<int, Item> itemsById, bool isDirect, string? note, int userId, DateTime nowUtc)
    {
        if (fromBranchId == toBranchId) throw new DomainException("A transfer needs two different locations.");
        if (lines.Count == 0) throw new DomainException("A transfer needs at least one item.");
        if (lines.GroupBy(l => l.ItemId).Any(g => g.Count() > 1)) throw new DomainException("Each item can appear only once.");

        var transfer = new Transfer
        {
            TenantId = tenantId, FromBranchId = fromBranchId, ToBranchId = toBranchId, IsDirect = isDirect,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), Status = TransferStatus.Requested,
            RequestedBy = userId, RequestedAtUtc = nowUtc
        };

        foreach (var (request, index) in lines.Select((l, i) => (l, i)))
        {
            if (!itemsById.TryGetValue(request.ItemId, out var item))
                throw new DomainException($"Line {index + 1}: item {request.ItemId} was not found.");
            transfer._lines.Add(TransferLine.Create(index + 1, item, request));
        }

        if (isDirect) transfer.Approve(userId, nowUtc);
        return transfer;
    }

    /// <summary>Attaches the children read from the database.</summary>
    public void Load(IEnumerable<TransferLine> lines, IEnumerable<TransferBatch> batches)
    {
        _lines.AddRange(lines);
        _batches.AddRange(batches);
    }

    #endregion

    #region Behaviour

    public void Approve(int userId, DateTime nowUtc)
    {
        MoveTo(TransferStatus.Approved);
        ApprovedBy = userId;
        ApprovedAtUtc = nowUtc;
    }

    public void Reject(string reason, int userId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("The reason is required.");
        MoveTo(TransferStatus.Rejected);
        ApprovedBy = userId;
        ApprovedAtUtc = nowUtc;
        ClosingNote = reason.Trim();
    }

    public void Cancel(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("The reason is required.");
        MoveTo(TransferStatus.Cancelled);
        ClosingNote = reason.Trim();
    }

    /// <summary>
    /// MBR-03 pick and ship: takes stock from the source's sellable batches, First-Expiry-First-Out,
    /// up to the requested quantity. A line with less stock ships what there is; a transfer with no
    /// stock at all is refused. BR-MBR-1: from now on the stock belongs to neither location.
    /// </summary>
    public void Ship(IReadOnlyCollection<Batch> sourceBatches, DateOnly today, int userId, DateTime nowUtc)
    {
        if (Status != TransferStatus.Approved) throw new DomainException($"Only an approved transfer can be shipped (it is {Status}).");

        foreach (var line in _lines)
        {
            var remaining = line.RequestedQuantity;
            foreach (var batch in sourceBatches.Where(b => b.ItemId == line.ItemId && b.IsSellable(today))
                         .OrderBy(b => b.ExpiryDate).ThenBy(b => b.Id))
            {
                var take = Math.Min(batch.Quantity, remaining);
                if (take == 0) break;
                _batches.Add(TransferBatch.Create(line.LineNumber, batch, take));
                batch.Take(take);
                remaining -= take;
            }
        }

        if (_batches.Count == 0) throw new DomainException("None of the items is in stock at the source.");

        MoveTo(TransferStatus.InTransit);
        ShippedBy = userId;
        ShippedAtUtc = nowUtc;
    }

    /// <summary>
    /// MBR-03 / AC-MBR-1: the receiver counts every shipped batch. What did not arrive is a shortage
    /// and needs a note; the transfer then closes as "received with shortage".
    /// </summary>
    public void Receive(IReadOnlyCollection<TransferReceiptLine> counted, string? note, int userId, DateTime nowUtc)
    {
        if (Status != TransferStatus.InTransit) throw new DomainException($"Only a transfer in transit can be received (it is {Status}).");

        foreach (var batch in _batches)
        {
            var line = counted.FirstOrDefault(c => c.SourceBatchId == batch.SourceBatchId)
                ?? throw new DomainException($"Batch {batch.BatchNumber} was shipped: enter how much arrived, even if 0.");
            batch.Receive(line.QuantityReceived);
        }

        var hasShortage = _batches.Any(b => b.Shortage > 0);
        if (hasShortage && string.IsNullOrWhiteSpace(note))
            throw new DomainException("Less than shipped arrived. Write what was found before posting.");

        MoveTo(hasShortage ? TransferStatus.ReceivedWithShortage : TransferStatus.Received);
        ReceivedBy = userId;
        ReceivedAtUtc = nowUtc;
        ClosingNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    #endregion

    #region Derived values

    /// <summary>Stock leaving the source at shipping, one movement per batch.</summary>
    public IEnumerable<StockMovement> ShipMovements() => _batches.Select(b =>
        new StockMovement(b.SourceBatchId, -b.QuantitySent, MovementType.TransferOut, "Transfer", Id));

    public int TotalShortage => _batches.Sum(b => b.Shortage);

    #endregion
}

#endregion
