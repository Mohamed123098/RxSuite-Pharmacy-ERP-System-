using RxSuite.Domain.Common;

namespace RxSuite.Domain.Controlled;

// BRD 8.7 "Controlled and narcotic drugs": the digital narcotics and psychotropics register.
// CTL-04 running balance, CTL-05 append-only with corrections, CTL-06 balance checks,
// BR-CTL-2 nobody can delete an entry. The database enforces the last rule with a trigger.

#region Enums

/// <summary>CTL-04: why the balance moved. Quantity is signed: in is positive, out is negative.</summary>
public enum RegisterEntryType : byte
{
    Purchase = 1,     // in: goods received
    Dispense = 2,     // out: sold to a patient against a prescription
    Destruction = 3,  // out: expired or damaged stock destroyed
    Adjustment = 4,   // in or out: a stock count correction
    Correction = 5    // in, out or zero: corrects an earlier entry and points to it (CTL-05)
}

#endregion

#region Register entry

/// <summary>
/// One line of the register (BRD key data "Controlled register entry"). Created whole and never
/// changed. The running balance is worked out by the database when the entry is written, under a
/// lock, so two pharmacists dispensing at the same moment cannot produce the same balance.
/// </summary>
public sealed class ControlledRegisterEntry : TenantEntity<long>
{
    public int BranchId { get; private set; }
    public int ItemId { get; private set; }
    public int? BatchId { get; private set; }
    public RegisterEntryType Type { get; private set; }

    /// <summary>Signed, in the smallest unit (BR-INV-1).</summary>
    public int Quantity { get; private set; }

    /// <summary>The balance after this entry. Filled by the database.</summary>
    public int Balance { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }
    public string DocumentType { get; private set; } = string.Empty;
    public long DocumentId { get; private set; }

    // CTL-03: who received it and on whose prescription.
    public int? PatientId { get; private set; }
    public string? PatientName { get; private set; }
    public string? PatientNationalId { get; private set; }
    public string? PrescriberName { get; private set; }
    public string? PrescriberLicence { get; private set; }
    public int? PrescriptionId { get; private set; }
    public string? PrescriptionNumber { get; private set; }
    public DateOnly? PrescriptionDate { get; private set; }
    public int? PrescriptionImageId { get; private set; }

    /// <summary>CTL-02: the pharmacist responsible for the movement.</summary>
    public int PharmacistId { get; private set; }

    /// <summary>CTL-05: the entry this one corrects.</summary>
    public long? CorrectsEntryId { get; private set; }
    public string? Note { get; private set; }

    private ControlledRegisterEntry() { }

    #region Factories (one per kind of movement, so each carries what the BRD asks for)

    /// <summary>CTL-03 / AC-CTL-1: a dispense, with the patient, the prescriber and the prescription.</summary>
    public static ControlledRegisterEntry ForDispense(int tenantId, int branchId, int itemId, int batchId, int quantity,
        long saleId, DispenseParty party, int pharmacistId, DateTime nowUtc)
    {
        if (quantity <= 0) throw new DomainException("A dispensed quantity must be greater than zero.");
        party.EnsureComplete();

        return new ControlledRegisterEntry
        {
            TenantId = tenantId, BranchId = branchId, ItemId = itemId, BatchId = batchId,
            Type = RegisterEntryType.Dispense, Quantity = -quantity, OccurredAtUtc = nowUtc,
            DocumentType = "Sale", DocumentId = saleId, PatientId = party.PatientId, PatientName = party.PatientName,
            PatientNationalId = party.PatientNationalId, PrescriberName = party.PrescriberName,
            PrescriberLicence = party.PrescriberLicence, PrescriptionId = party.PrescriptionId,
            PrescriptionNumber = party.PrescriptionNumber, PrescriptionDate = party.PrescriptionDate,
            PrescriptionImageId = party.PrescriptionImageId, PharmacistId = pharmacistId
        };
    }

    /// <summary>A stock movement that is not a dispense: purchase, destruction or count adjustment.</summary>
    public static ControlledRegisterEntry ForStockMovement(int tenantId, int branchId, int itemId, int batchId,
        RegisterEntryType type, int quantity, string documentType, long documentId, int pharmacistId, string? note,
        DateTime nowUtc)
    {
        if (type is RegisterEntryType.Dispense or RegisterEntryType.Correction)
            throw new DomainException("Use the dispense or correction entry for this movement.");
        if (quantity == 0) throw new DomainException("A register entry must move the balance.");
        if (type == RegisterEntryType.Purchase && quantity < 0) throw new DomainException("A purchase adds stock.");
        if (type == RegisterEntryType.Destruction && quantity > 0) throw new DomainException("A destruction removes stock.");

        return new ControlledRegisterEntry
        {
            TenantId = tenantId, BranchId = branchId, ItemId = itemId, BatchId = batchId, Type = type,
            Quantity = quantity, OccurredAtUtc = nowUtc, DocumentType = documentType, DocumentId = documentId,
            PharmacistId = pharmacistId, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()
        };
    }

    /// <summary>
    /// CTL-05: corrects an earlier entry by adding a new one that points to it. The quantity can be
    /// zero when only the written details were wrong; the reason is always required.
    /// </summary>
    public static ControlledRegisterEntry Correction(ControlledRegisterEntry original, int quantityChange, string reason,
        int pharmacistId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A correction needs a reason.");
        if (original.Type == RegisterEntryType.Correction)
            throw new DomainException("Correct the original entry, not another correction.");

        return new ControlledRegisterEntry
        {
            TenantId = original.TenantId, BranchId = original.BranchId, ItemId = original.ItemId,
            BatchId = original.BatchId, Type = RegisterEntryType.Correction, Quantity = quantityChange,
            OccurredAtUtc = nowUtc, DocumentType = "RegisterEntry", DocumentId = original.Id,
            PharmacistId = pharmacistId, CorrectsEntryId = original.Id, Note = reason.Trim()
        };
    }

    #endregion
}

/// <summary>CTL-03: the people and the prescription behind a controlled dispense.</summary>
public sealed record DispenseParty(int PatientId, string PatientName, string? PatientNationalId, string PrescriberName,
    string? PrescriberLicence, int PrescriptionId, string? PrescriptionNumber, DateOnly PrescriptionDate,
    int? PrescriptionImageId)
{
    /// <summary>Lists what is missing, in words a pharmacist can act on. Empty means complete.</summary>
    public IReadOnlyList<string> MissingDetails()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(PatientNationalId)) missing.Add("the patient's national ID");
        if (string.IsNullOrWhiteSpace(PrescriberLicence)) missing.Add("the prescriber's licence number");
        if (string.IsNullOrWhiteSpace(PrescriptionNumber)) missing.Add("the prescription number");
        if (PrescriptionImageId is null) missing.Add("an image of the prescription");
        return missing;
    }

    public void EnsureComplete()
    {
        var missing = MissingDetails();
        if (missing.Count > 0)
            throw new DomainException($"A controlled medicine needs {string.Join(", ", missing)} before it is dispensed.");
    }
}

#endregion

#region Balance check (CTL-06, US-CTL-3)

/// <summary>
/// A pharmacist counting what is physically in the locked cabinet and signing it with their PIN.
/// Any difference with the register or with the stock record is a mismatch for the owner to see.
/// </summary>
public sealed class ControlledBalanceCheck : TenantEntity<int>
{
    public int BranchId { get; private set; }
    public int ItemId { get; private set; }
    public int CountedQuantity { get; private set; }
    public int RegisterBalance { get; private set; }
    public int StockQuantity { get; private set; }
    public int PharmacistId { get; private set; }
    public string? Note { get; private set; }
    public DateTime CheckedAtUtc { get; private set; }

    private ControlledBalanceCheck() { }

    public static ControlledBalanceCheck Create(int tenantId, int branchId, int itemId, int countedQuantity,
        int registerBalance, int stockQuantity, int pharmacistId, string? note, DateTime nowUtc)
    {
        if (countedQuantity < 0) throw new DomainException("The counted quantity cannot be negative.");

        var check = new ControlledBalanceCheck
        {
            TenantId = tenantId, BranchId = branchId, ItemId = itemId, CountedQuantity = countedQuantity,
            RegisterBalance = registerBalance, StockQuantity = stockQuantity, PharmacistId = pharmacistId,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), CheckedAtUtc = nowUtc
        };

        // US-CTL-3: a mismatch must be explained on the spot.
        if (check.IsMismatch && check.Note is null)
            throw new DomainException("The count does not match the register. Write what was found before signing.");
        return check;
    }

    public bool IsMismatch => CountedQuantity != RegisterBalance || RegisterBalance != StockQuantity;
}

#endregion
