using RxSuite.Domain.Catalog;
using RxSuite.Domain.Common;

namespace RxSuite.Domain.Clinical;

// BRD 8.6 "Prescriptions and drug database": patients (US-RX-3, RX-08), prescribers (RX-04, RX-09),
// prescriptions with partial dispensing and refills (RX-04, RX-06, AC-RX-2) and the dispense record
// with the alerts shown and the overrides given (key data "Dispense", RX-10, BR-RX-2).

#region Patient

/// <summary>One recorded allergy: an ingredient code (AMOXICILLIN) or a class code (PENICILLINS).</summary>
public sealed record PatientAllergy(string AllergenCode, string? Note);

/// <summary>
/// AGGREGATE ROOT. A patient of the tenant, shared by all its branches so the medication history
/// follows them (RX-08). Owns the allergy list.
/// </summary>
public sealed class Patient : TenantEntity<int>
{
    private readonly List<PatientAllergy> _allergies = [];

    public string FullName { get; private set; } = string.Empty;

    /// <summary>CTL-03: required before a controlled medicine is dispensed.</summary>
    public string? NationalId { get; private set; }
    public string? Mobile { get; private set; }
    public DateOnly? BirthDate { get; private set; }
    public IReadOnlyList<PatientAllergy> Allergies => _allergies;

    private Patient() { }

    public static Patient Create(int tenantId, string fullName, string? nationalId, string? mobile, DateOnly? birthDate,
        IEnumerable<PatientAllergy> allergies, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(fullName)) throw new DomainException("The patient's name is required.");
        if (nationalId is { Length: > 0 } id && (id.Trim().Length != 14 || !id.Trim().All(char.IsAsciiDigit)))
            throw new DomainException("An Egyptian national ID has 14 digits.");
        if (birthDate > today) throw new DomainException("The birth date cannot be in the future.");

        var patient = new Patient
        {
            TenantId = tenantId, FullName = fullName.Trim(), NationalId = Clean(nationalId), Mobile = Clean(mobile),
            BirthDate = birthDate
        };
        foreach (var allergy in allergies) patient.AddAllergy(allergy.AllergenCode, allergy.Note);
        return patient;
    }

    /// <summary>US-RX-3: records an allergy once; codes are stored in upper case to match the drug database.</summary>
    public bool AddAllergy(string allergenCode, string? note)
    {
        if (string.IsNullOrWhiteSpace(allergenCode)) throw new DomainException("The allergen code is required.");
        var code = allergenCode.Trim().ToUpperInvariant();
        if (_allergies.Any(a => a.AllergenCode == code)) return false;

        _allergies.Add(new PatientAllergy(code, Clean(note)));
        return true;
    }

    /// <summary>Attaches the allergies read from the database.</summary>
    public void LoadAllergies(IEnumerable<PatientAllergy> allergies) => _allergies.AddRange(allergies);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

#endregion

#region Prescriber

/// <summary>A doctor who writes prescriptions (RX-04, RX-09). The licence number is needed for controlled items (CTL-03).</summary>
public sealed class Prescriber : TenantEntity<int>
{
    public string FullName { get; private set; } = string.Empty;
    public string? Specialty { get; private set; }
    public string? LicenceNumber { get; private set; }
    public string? Clinic { get; private set; }

    private Prescriber() { }

    public static Prescriber Create(int tenantId, string fullName, string? specialty, string? licenceNumber, string? clinic)
    {
        if (string.IsNullOrWhiteSpace(fullName)) throw new DomainException("The prescriber's name is required.");
        return new Prescriber
        {
            TenantId = tenantId, FullName = fullName.Trim(), Specialty = specialty?.Trim(),
            LicenceNumber = string.IsNullOrWhiteSpace(licenceNumber) ? null : licenceNumber.Trim(), Clinic = clinic?.Trim()
        };
    }
}

#endregion

#region Prescription

public enum PrescriptionStatus : byte { Open = 1, PartiallyDispensed = 2, Completed = 3, Cancelled = 4 }

/// <summary>What the pharmacist types for one prescribed medicine.</summary>
/// <param name="Quantity">Quantity per fill, in <paramref name="Unit"/>.</param>
/// <param name="DoseUnits">Smallest units per dose (1 tablet, 5 ml). Used by the max-dose check and the label.</param>
/// <param name="Instructions">Free text for the label when the structured dose is not enough.</param>
public sealed record PrescriptionLineRequest(int ItemId, UnitType Unit, int Quantity, int RefillsAllowed,
    decimal? DoseUnits, int? FrequencyPerDay, int? DurationDays, string? Instructions);

/// <summary>
/// One prescribed medicine. Everything is counted in the smallest unit underneath, so a box can be
/// dispensed strip by strip (RX-06).
/// </summary>
public sealed class PrescriptionLine
{
    public int LineNumber { get; private set; }
    public int ItemId { get; private set; }
    public string ItemName { get; private set; } = string.Empty;
    public UnitType Unit { get; private set; }
    public int Quantity { get; private set; }
    public int RefillsAllowed { get; private set; }

    /// <summary>Smallest units in one prescribed unit, frozen when the prescription is recorded.</summary>
    public int BaseUnitsPerUnit { get; private set; }
    public decimal? DoseUnits { get; private set; }
    public int? FrequencyPerDay { get; private set; }
    public int? DurationDays { get; private set; }
    public string? Instructions { get; private set; }
    public int DispensedBaseUnits { get; private set; }

    private PrescriptionLine() { }

    internal static PrescriptionLine Create(int lineNumber, Item item, PrescriptionLineRequest r)
    {
        if (r.Quantity <= 0) throw new DomainException($"Line {lineNumber}: the quantity must be greater than zero.");
        if (r.RefillsAllowed is < 0 or > 12) throw new DomainException($"Line {lineNumber}: refills must be between 0 and 12.");
        if (r.DoseUnits <= 0 || r.FrequencyPerDay <= 0 || r.DurationDays <= 0)
            throw new DomainException($"Line {lineNumber}: dose, frequency and duration must be greater than zero.");
        if (!Enum.IsDefined(r.Unit)) throw new DomainException($"Line {lineNumber}: the unit is not valid.");

        return new PrescriptionLine
        {
            LineNumber = lineNumber, ItemId = item.Id, ItemName = item.DisplayName, Unit = r.Unit, Quantity = r.Quantity,
            RefillsAllowed = r.RefillsAllowed, BaseUnitsPerUnit = item.BaseUnitsPer(r.Unit), DoseUnits = r.DoseUnits,
            FrequencyPerDay = r.FrequencyPerDay, DurationDays = r.DurationDays,
            Instructions = string.IsNullOrWhiteSpace(r.Instructions) ? null : r.Instructions.Trim()
        };
    }

    /// <summary>RX-06: the first fill plus every refill.</summary>
    public int AllowedBaseUnits => Quantity * (1 + RefillsAllowed) * BaseUnitsPerUnit;
    public int RemainingBaseUnits => AllowedBaseUnits - DispensedBaseUnits;

    /// <summary>AC-RX-2: what is left, in the unit the doctor wrote (2 boxes, 1.5 boxes...).</summary>
    public decimal RemainingInUnit => (decimal)RemainingBaseUnits / BaseUnitsPerUnit;

    internal void Dispense(int baseUnits)
    {
        if (baseUnits <= 0) throw new DomainException("The dispensed quantity must be greater than zero.");
        if (baseUnits > RemainingBaseUnits)
            throw new DomainException($"{ItemName}: only {RemainingInUnit:0.##} {Unit} left on the prescription.");
        DispensedBaseUnits += baseUnits;
    }
}

/// <summary>Metadata of an attached prescription image (RX-05). The encrypted bytes live in the image store.</summary>
public sealed record PrescriptionImageInfo(int Id, string ContentType, int SizeBytes, DateTime UploadedAtUtc);

/// <summary>
/// AGGREGATE ROOT. A prescription as written by the doctor (RX-04). It tracks what has been
/// dispensed against each line, across visits and branches (RX-06).
/// </summary>
public sealed class Prescription : TenantEntity<int>
{
    #region State

    private readonly List<PrescriptionLine> _lines = [];
    private readonly List<PrescriptionImageInfo> _images = [];

    public int PatientId { get; private set; }
    public int PrescriberId { get; private set; }
    public int BranchId { get; private set; }

    /// <summary>The number written on the paper prescription, if any (CTL-03 needs it for controlled items).</summary>
    public string? PrescriptionNumber { get; private set; }
    public DateOnly PrescribedOn { get; private set; }
    public string? Diagnosis { get; private set; }
    public PrescriptionStatus Status { get; private set; }
    public int RecordedBy { get; private set; }
    public DateTime RecordedAtUtc { get; private set; }
    public IReadOnlyList<PrescriptionLine> Lines => _lines;
    public IReadOnlyList<PrescriptionImageInfo> Images => _images;

    private Prescription() { }

    #endregion

    #region Factory

    public static Prescription Create(int tenantId, int branchId, int patientId, int prescriberId, string? number,
        DateOnly prescribedOn, string? diagnosis, IReadOnlyList<PrescriptionLineRequest> lines,
        IReadOnlyDictionary<int, Item> itemsById, int recordedBy, DateTime nowUtc, DateOnly today)
    {
        if (lines.Count == 0) throw new DomainException("A prescription needs at least one medicine.");
        if (prescribedOn > today) throw new DomainException("The prescription date cannot be in the future.");

        var prescription = new Prescription
        {
            TenantId = tenantId, BranchId = branchId, PatientId = patientId, PrescriberId = prescriberId,
            PrescriptionNumber = string.IsNullOrWhiteSpace(number) ? null : number.Trim(), PrescribedOn = prescribedOn,
            Diagnosis = string.IsNullOrWhiteSpace(diagnosis) ? null : diagnosis.Trim(), Status = PrescriptionStatus.Open,
            RecordedBy = recordedBy, RecordedAtUtc = nowUtc
        };

        foreach (var (request, index) in lines.Select((l, i) => (l, i)))
        {
            if (!itemsById.TryGetValue(request.ItemId, out var item))
                throw new DomainException($"Line {index + 1}: item {request.ItemId} was not found.");
            prescription._lines.Add(PrescriptionLine.Create(index + 1, item, request));
        }

        return prescription;
    }

    /// <summary>Attaches the children read from the database.</summary>
    public void Load(IEnumerable<PrescriptionLine> lines, IEnumerable<PrescriptionImageInfo> images)
    {
        _lines.AddRange(lines);
        _images.AddRange(images);
    }

    #endregion

    #region Behaviour

    public bool CanDispense => Status is PrescriptionStatus.Open or PrescriptionStatus.PartiallyDispensed;
    public bool HasImage => _images.Count > 0;

    public PrescriptionLine Line(int lineNumber) => _lines.FirstOrDefault(l => l.LineNumber == lineNumber)
        ?? throw new DomainException($"Line {lineNumber} is not on the prescription.");

    /// <summary>RX-06 / AC-RX-2: records what left the pharmacy against each line and moves the status on.</summary>
    public void Dispense(IEnumerable<(int LineNumber, int ItemId, int BaseUnits)> parts)
    {
        if (!CanDispense) throw new DomainException($"This prescription is {Status} and cannot be dispensed.");

        foreach (var (lineNumber, itemId, baseUnits) in parts)
        {
            var line = Line(lineNumber);
            if (line.ItemId != itemId)
                throw new DomainException($"Line {lineNumber} of the prescription is for {line.ItemName}, not this item.");
            line.Dispense(baseUnits);
        }

        Status = _lines.All(l => l.RemainingBaseUnits == 0) ? PrescriptionStatus.Completed : PrescriptionStatus.PartiallyDispensed;
    }

    public void Cancel()
    {
        if (Status == PrescriptionStatus.Completed) throw new DomainException("A completed prescription cannot be cancelled.");
        Status = PrescriptionStatus.Cancelled;
    }

    #endregion
}

#endregion

#region Dispense

/// <summary>
/// One prescription medicine served by a sale line. The prescription line is null when the item
/// was sold under "record later" (BR-RX-1) and the prescription is still owed.
/// </summary>
public sealed record DispenseLine(int? PrescriptionLineNumber, int SaleLineNumber, int ItemId, int BaseUnits);

/// <summary>
/// The record of one dispensing visit (BRD key data "Dispense"): which prescription, which sale,
/// who dispensed, the alerts that were on screen and the overrides with their reasons.
/// It is written once and never changed.
/// </summary>
public sealed class Dispense : TenantEntity<long>
{
    private readonly List<DispenseLine> _lines = [];
    private readonly List<ClinicalAlert> _alerts = [];
    private readonly List<AlertOverride> _overrides = [];

    public int BranchId { get; private set; }
    public int? PrescriptionId { get; private set; }
    public int? PatientId { get; private set; }
    public long SaleId { get; private set; }
    public int PharmacistId { get; private set; }

    /// <summary>RX-10: optional counselling notes.</summary>
    public string? CounsellingNote { get; private set; }

    /// <summary>BR-RX-1: an Rx-only item sold under "record later"; the prescription is still owed.</summary>
    public bool PrescriptionPending { get; private set; }
    public DateTime DispensedAtUtc { get; private set; }
    public IReadOnlyList<DispenseLine> Lines => _lines;
    public IReadOnlyList<ClinicalAlert> Alerts => _alerts;
    public IReadOnlyList<AlertOverride> Overrides => _overrides;

    private Dispense() { }

    public static Dispense Create(int tenantId, int branchId, int? prescriptionId, int? patientId, int pharmacistId,
        IEnumerable<DispenseLine> lines, IEnumerable<ClinicalAlert> alerts, IEnumerable<AlertOverride> overrides,
        string? counsellingNote, bool prescriptionPending, DateTime nowUtc)
    {
        var alertList = alerts.ToList();
        var dispense = new Dispense
        {
            TenantId = tenantId, BranchId = branchId, PrescriptionId = prescriptionId, PatientId = patientId,
            PharmacistId = pharmacistId, PrescriptionPending = prescriptionPending, DispensedAtUtc = nowUtc,
            CounsellingNote = string.IsNullOrWhiteSpace(counsellingNote) ? null : counsellingNote.Trim()
        };
        dispense._lines.AddRange(lines);
        dispense._alerts.AddRange(alertList);

        // Only the overrides that answer an alert on screen are kept, each once.
        dispense._overrides.AddRange(overrides
            .Where(o => !string.IsNullOrWhiteSpace(o.Reason) && alertList.Any(a => a.Code.Equals(o.AlertCode, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(o => o.AlertCode.ToUpperInvariant())
            .Select(o => o with { Reason = o.Reason.Trim() }));
        return dispense;
    }

    /// <summary>The sale is saved first; its Id is attached afterwards in the same transaction.</summary>
    public void AttachSale(long saleId) => SaleId = saleId;
}

#endregion
