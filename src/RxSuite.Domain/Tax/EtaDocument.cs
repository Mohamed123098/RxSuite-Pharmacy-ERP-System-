using RxSuite.Domain.Common;

namespace RxSuite.Domain.Tax;

// BRD 8.9 ACC-03..07 and Figure 15 "ETA document lifecycle".

#region Enums

/// <summary>BRD key data "ETA document" types built so far.</summary>
public enum EtaDocumentType : byte { Receipt = 1, Invoice = 2, ReturnReceipt = 3, CreditNote = 4 }

/// <summary>ACC-04 statuses.</summary>
public enum EtaDocumentStatus : byte { Draft = 1, Queued = 2, Submitted = 3, Valid = 4, Invalid = 5, Cancelled = 6 }

#endregion

#region ETA document

/// <summary>
/// AGGREGATE ROOT. The tax document of a sale or a return and its status at the Egyptian Tax
/// Authority. STATE pattern in its simple form: every status change goes through
/// <see cref="MoveTo"/>, which only allows the arrows drawn in Figure 15.
/// </summary>
public sealed class EtaDocument : TenantEntity<long>
{
    #region State

    public int BranchId { get; private set; }
    public EtaDocumentType Type { get; private set; }
    public long? SaleId { get; private set; }
    public long? SaleReturnId { get; private set; }

    /// <summary>ACC-06: for a return, the document of the original sale.</summary>
    public long? OriginalDocumentId { get; private set; }
    public EtaDocumentStatus Status { get; private set; }
    public string? Uuid { get; private set; }
    public string? LongId { get; private set; }
    public string? QrCode { get; private set; }
    public string? Errors { get; private set; }
    public int Attempts { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? SubmittedAtUtc { get; private set; }

    private EtaDocument() { }

    #endregion

    #region Factories

    /// <summary>BR-ACC-3: B2B buyers get an e-invoice, everyone else an e-receipt.</summary>
    public static EtaDocument ForSale(int tenantId, int branchId, long saleId, bool isBusinessSale, DateTime nowUtc) =>
        new()
        {
            TenantId = tenantId, BranchId = branchId, SaleId = saleId,
            Type = isBusinessSale ? EtaDocumentType.Invoice : EtaDocumentType.Receipt,
            Status = EtaDocumentStatus.Queued, CreatedAtUtc = nowUtc
        };

    /// <summary>ACC-06 / AC-ACC-2: a return document always points at the original document.</summary>
    public static EtaDocument ForReturn(int tenantId, int branchId, long saleReturnId, EtaDocument? original, DateTime nowUtc) =>
        new()
        {
            TenantId = tenantId, BranchId = branchId, SaleReturnId = saleReturnId,
            OriginalDocumentId = original?.Id,
            Type = original?.Type == EtaDocumentType.Invoice ? EtaDocumentType.CreditNote : EtaDocumentType.ReturnReceipt,
            Status = EtaDocumentStatus.Queued, CreatedAtUtc = nowUtc
        };

    #endregion

    #region Status flow (Figure 15)

    private static readonly Dictionary<EtaDocumentStatus, EtaDocumentStatus[]> AllowedMoves = new()
    {
        [EtaDocumentStatus.Draft] = [EtaDocumentStatus.Queued],
        [EtaDocumentStatus.Queued] = [EtaDocumentStatus.Submitted, EtaDocumentStatus.Valid, EtaDocumentStatus.Invalid],
        [EtaDocumentStatus.Submitted] = [EtaDocumentStatus.Valid, EtaDocumentStatus.Invalid],
        [EtaDocumentStatus.Invalid] = [EtaDocumentStatus.Queued],
        [EtaDocumentStatus.Valid] = [EtaDocumentStatus.Cancelled],
        [EtaDocumentStatus.Cancelled] = []
    };

    private void MoveTo(EtaDocumentStatus next)
    {
        if (!AllowedMoves[Status].Contains(next))
            throw new DomainException($"An ETA document cannot go from {Status} to {next}.");
        Status = next;
    }

    /// <summary>ETA accepted the document: it now has a UUID and a QR code.</summary>
    public void MarkValid(string uuid, string? longId, string? qrCode, DateTime nowUtc)
    {
        MoveTo(EtaDocumentStatus.Valid);
        Uuid = uuid;
        LongId = longId;
        QrCode = qrCode;
        Errors = null;
        Attempts++;
        SubmittedAtUtc = nowUtc;
    }

    /// <summary>ETA rejected the document: keep its messages for the error dashboard (ACC-05).</summary>
    public void MarkInvalid(string errors, DateTime nowUtc)
    {
        MoveTo(EtaDocumentStatus.Invalid);
        Errors = errors;
        Attempts++;
        SubmittedAtUtc = nowUtc;
    }

    /// <summary>ETA could not be reached: stay queued and count the attempt (ACC-07).</summary>
    public void RegisterFailedAttempt() => Attempts++;

    /// <summary>AC-ACC-1: after the data is fixed the document goes back to the queue.</summary>
    public void Requeue()
    {
        MoveTo(EtaDocumentStatus.Queued);
        Errors = null;
    }

    /// <summary>BR-ACC-2: a valid document is never edited, only cancelled.</summary>
    public void Cancel() => MoveTo(EtaDocumentStatus.Cancelled);

    #endregion
}

#endregion
