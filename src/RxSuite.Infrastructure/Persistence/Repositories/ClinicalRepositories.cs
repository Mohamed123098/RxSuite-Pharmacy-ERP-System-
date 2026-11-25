using RxSuite.Application.Abstractions;
using RxSuite.Application.Features.Clinical;
using RxSuite.Domain.Clinical;
using RxSuite.Infrastructure.Persistence.Connection;

namespace RxSuite.Infrastructure.Persistence.Repositories;

#region Drug database

/// <summary>
/// REPOSITORY for the central drug database (RX-01). Its procedures take no tenant because the
/// database is shared; only the item-to-drug lookup is scoped to the tenant's items.
/// </summary>
internal sealed class DrugRepository(IDbSession session, ICurrentUser currentUser)
    : StoredProcedureRepository(session, currentUser), IDrugRepository
{
    /// <summary>Shape of a row of usp_Drug_GetClinicalProfiles: one item and one of its ingredients.</summary>
    private sealed class ProfileRow
    {
        public int ItemId { get; set; }
        public string ItemName { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? ClassCode { get; set; }
        public decimal? StrengthMgPerUnit { get; set; }
        public decimal? MaxDailyDoseMg { get; set; }
        public int? MinAgeYears { get; set; }
    }

    /// <summary>Shape of a row of usp_Drug_GetInteractions.</summary>
    private sealed class InteractionRow
    {
        public string IngredientA { get; set; } = string.Empty;
        public string IngredientB { get; set; } = string.Empty;
        public byte Severity { get; set; }
        public string Description { get; set; } = string.Empty;
        public string? Recommendation { get; set; }
    }

    public Task<DrugDto?> GetDrugAsync(int drugId, CancellationToken ct) =>
        QuerySingleOrDefaultAsync<DrugDto>("dbo.usp_Drug_GetById", new { DrugId = drugId }, ct);

    public Task<IReadOnlyList<DrugDto>> SearchDrugsAsync(string term, CancellationToken ct) =>
        QueryAsync<DrugDto>("dbo.usp_Drug_Search", new { Term = term }, ct);

    public async Task<IReadOnlyList<ItemClinicalProfile>> GetClinicalProfilesAsync(IEnumerable<int> itemIds, CancellationToken ct)
    {
        var rows = await QueryAsync<ProfileRow>("dbo.usp_Drug_GetClinicalProfiles", Scoped(new { ItemIdsJson = Json(itemIds) }), ct);

        return rows.GroupBy(r => r.ItemId)
            .Select(g => new ItemClinicalProfile(g.Key, g.First().ItemName,
                g.Select(r => new IngredientInfo(r.Code, r.Name, r.ClassCode, r.StrengthMgPerUnit, r.MaxDailyDoseMg, r.MinAgeYears)).ToList()))
            .ToList();
    }

    public async Task<IReadOnlyList<DrugInteraction>> GetInteractionsAsync(IEnumerable<string> ingredientCodes, CancellationToken ct)
    {
        var rows = await QueryAsync<InteractionRow>("dbo.usp_Drug_GetInteractions", new { IngredientCodesJson = Json(ingredientCodes) }, ct);
        return rows.Select(r => new DrugInteraction(r.IngredientA, r.IngredientB, (AlertSeverity)r.Severity, r.Description,
            r.Recommendation)).ToList();
    }
}

#endregion

#region Patients and prescribers

/// <summary>REPOSITORY for patients, their allergies and their medication history.</summary>
internal sealed class PatientRepository(IDbSession session, ICurrentUser currentUser)
    : StoredProcedureRepository(session, currentUser), IPatientRepository
{
    #region Commands

    public async Task<int> AddAsync(Patient p, CancellationToken ct)
    {
        var id = await ExecuteScalarAsync<int>("dbo.usp_Patient_Insert", Scoped(new
        {
            p.FullName, p.NationalId, p.Mobile, p.BirthDate,
            AllergiesJson = Json(p.Allergies.Select(a => new { a.AllergenCode, a.Note }))
        }), ct);

        p.AssignId(id);
        return id;
    }

    public Task AddAllergyAsync(int patientId, PatientAllergy allergy, CancellationToken ct) =>
        ExecuteAsync("dbo.usp_PatientAllergy_Insert",
            Scoped(new { PatientId = patientId, allergy.AllergenCode, allergy.Note }), ct);

    #endregion

    #region Queries

    /// <summary>Result sets: 1 the patient, 2 the allergies.</summary>
    public Task<Patient?> GetByIdAsync(int patientId, CancellationToken ct) =>
        QueryMultipleAsync("dbo.usp_Patient_GetById", Scoped(new { PatientId = patientId }), async grid =>
        {
            var patient = await grid.ReadSingleOrDefaultAsync<Patient>();
            var allergies = await grid.ReadAsync<PatientAllergy>();
            patient?.LoadAllergies(allergies);
            return patient;
        }, ct);

    public Task<bool> NationalIdExistsAsync(string nationalId, CancellationToken ct) =>
        ExecuteScalarAsync<bool>("dbo.usp_Patient_NationalIdExists", Scoped(new { NationalId = nationalId }), ct);

    public Task<IReadOnlyList<PatientSummaryDto>> SearchAsync(string term, CancellationToken ct) =>
        QueryAsync<PatientSummaryDto>("dbo.usp_Patient_Search", Scoped(new { Term = term }), ct);

    public Task<IReadOnlyList<int>> GetCurrentMedicationItemIdsAsync(int patientId, DateTime sinceUtc, int? exceptPrescriptionId,
        CancellationToken ct) =>
        QueryAsync<int>("dbo.usp_Patient_GetCurrentMedicationItemIds",
            Scoped(new { PatientId = patientId, SinceUtc = sinceUtc, ExceptPrescriptionId = exceptPrescriptionId }), ct);

    public Task<IReadOnlyList<MedicationHistoryLine>> GetMedicationHistoryAsync(int patientId, CancellationToken ct) =>
        QueryAsync<MedicationHistoryLine>("dbo.usp_Patient_GetMedicationHistory", Scoped(new { PatientId = patientId }), ct);

    #endregion
}

/// <summary>REPOSITORY for the prescriber directory.</summary>
internal sealed class PrescriberRepository(IDbSession session, ICurrentUser currentUser)
    : StoredProcedureRepository(session, currentUser), IPrescriberRepository
{
    public async Task<int> AddAsync(Prescriber p, CancellationToken ct)
    {
        var id = await ExecuteScalarAsync<int>("dbo.usp_Prescriber_Insert",
            Scoped(new { p.FullName, p.Specialty, p.LicenceNumber, p.Clinic }), ct);

        p.AssignId(id);
        return id;
    }

    public Task<Prescriber?> GetByIdAsync(int prescriberId, CancellationToken ct) =>
        QuerySingleOrDefaultAsync<Prescriber>("dbo.usp_Prescriber_GetById", Scoped(new { PrescriberId = prescriberId }), ct);

    public Task<IReadOnlyList<PrescriberDto>> SearchAsync(string term, CancellationToken ct) =>
        QueryAsync<PrescriberDto>("dbo.usp_Prescriber_Search", Scoped(new { Term = term }), ct);
}

#endregion

#region Prescriptions and dispenses

/// <summary>
/// REPOSITORY for prescriptions. A prescription and its lines are saved by ONE procedure call;
/// the lines travel as JSON.
/// </summary>
internal sealed class PrescriptionRepository(IDbSession session, ICurrentUser currentUser)
    : StoredProcedureRepository(session, currentUser), IPrescriptionRepository
{
    #region Commands

    public async Task<int> AddAsync(Prescription p, CancellationToken ct)
    {
        var id = await ExecuteScalarAsync<int>("dbo.usp_Prescription_Insert", Scoped(new
        {
            p.BranchId, p.PatientId, p.PrescriberId, p.PrescriptionNumber, p.PrescribedOn, p.Diagnosis,
            Status = (byte)p.Status, p.RecordedBy, p.RecordedAtUtc,
            LinesJson = Json(p.Lines.Select(l => new
            {
                l.LineNumber, l.ItemId, l.ItemName, Unit = (byte)l.Unit, l.Quantity, l.RefillsAllowed, l.BaseUnitsPerUnit,
                l.DoseUnits, l.FrequencyPerDay, l.DurationDays, l.Instructions
            }))
        }), ct);

        p.AssignId(id);
        return id;
    }

    public Task UpdateAsync(Prescription p, CancellationToken ct) =>
        ExecuteAsync("dbo.usp_Prescription_Update", Scoped(new
        {
            PrescriptionId = p.Id, Status = (byte)p.Status,
            LinesJson = Json(p.Lines.Select(l => new { l.LineNumber, l.DispensedBaseUnits }))
        }), ct);

    public Task<int> AddImageAsync(int prescriptionId, string storageKey, string contentType, int sizeBytes, int uploadedBy,
        DateTime uploadedAtUtc, CancellationToken ct) =>
        ExecuteScalarAsync<int>("dbo.usp_PrescriptionImage_Insert", Scoped(new
        {
            PrescriptionId = prescriptionId, StorageKey = storageKey, ContentType = contentType, SizeBytes = sizeBytes,
            UploadedBy = uploadedBy, UploadedAtUtc = uploadedAtUtc
        }), ct);

    #endregion

    #region Queries

    /// <summary>Result sets: 1 the prescription, 2 its lines, 3 its images.</summary>
    public Task<Prescription?> GetByIdAsync(int prescriptionId, CancellationToken ct) =>
        QueryMultipleAsync("dbo.usp_Prescription_GetById", Scoped(new { PrescriptionId = prescriptionId }), async grid =>
        {
            var prescription = await grid.ReadSingleOrDefaultAsync<Prescription>();
            var lines = await grid.ReadAsync<PrescriptionLine>();
            var images = await grid.ReadAsync<PrescriptionImageInfo>();
            prescription?.Load(lines, images);
            return prescription;
        }, ct);

    public Task<StoredPrescriptionImage?> GetImageAsync(int prescriptionId, int imageId, CancellationToken ct) =>
        QuerySingleOrDefaultAsync<StoredPrescriptionImage>("dbo.usp_PrescriptionImage_GetById",
            Scoped(new { PrescriptionId = prescriptionId, ImageId = imageId }), ct);

    public Task<IReadOnlyList<OpenPrescriptionDto>> GetOpenAsync(int? patientId, CancellationToken ct) =>
        QueryAsync<OpenPrescriptionDto>("dbo.usp_Prescription_GetOpen", Scoped(new { PatientId = patientId }), ct);

    #endregion
}

/// <summary>REPOSITORY for dispense records. Written once with their lines and alerts, never changed.</summary>
internal sealed class DispenseRepository(IDbSession session, ICurrentUser currentUser)
    : StoredProcedureRepository(session, currentUser), IDispenseRepository
{
    public async Task<long> AddAsync(Dispense d, CancellationToken ct)
    {
        var reasons = d.Overrides.ToDictionary(o => o.AlertCode, o => o.Reason, StringComparer.OrdinalIgnoreCase);

        var id = await ExecuteScalarAsync<long>("dbo.usp_Dispense_Insert", Scoped(new
        {
            d.BranchId, d.PrescriptionId, d.PatientId, d.SaleId, d.PharmacistId, d.CounsellingNote, d.PrescriptionPending,
            d.DispensedAtUtc,
            LinesJson = Json(d.Lines.Select(l => new { l.SaleLineNumber, l.PrescriptionLineNumber, l.ItemId, l.BaseUnits })),
            AlertsJson = Json(d.Alerts.Select(a => new
            {
                a.Code, Type = (byte)a.Type, Severity = (byte)a.Severity, a.Message,
                OverrideReason = reasons.GetValueOrDefault(a.Code)
            }))
        }), ct);

        d.AssignId(id);
        return id;
    }

    public Task<IReadOnlyList<DispenseLabelRow>> GetLabelRowsAsync(long saleId, CancellationToken ct) =>
        QueryAsync<DispenseLabelRow>("dbo.usp_Dispense_GetLabelRows", Scoped(new { SaleId = saleId }), ct);
}

#endregion
