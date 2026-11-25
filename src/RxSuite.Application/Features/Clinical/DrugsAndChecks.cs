using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Domain.Catalog;
using RxSuite.Domain.Clinical;
using RxSuite.Domain.Common;
using RxSuite.Domain.Identity;
using RxSuite.Domain.Rules;

namespace RxSuite.Application.Features.Clinical;

// USE CASES: search the central drug database (RX-01) and run the clinical checks for the alerts
// panel (RX-02, RX-03, AC-RX-1) before anything is dispensed.

#region Read model

/// <summary>A medicine in the central drug database (RX-01).</summary>
public sealed class DrugDto
{
    public int Id { get; set; }
    public string TradeName { get; set; } = string.Empty;
    public string Strength { get; set; } = string.Empty;
    public string DosageForm { get; set; } = string.Empty;
    public string? AtcCode { get; set; }
    public string? Manufacturer { get; set; }
    public string? EdaRegistrationNumber { get; set; }
    public decimal? OfficialPrice { get; set; }
    public bool IsRxOnly { get; set; }
    public ControlSchedule ControlSchedule { get; set; }
    public string? PregnancyCategory { get; set; }

    /// <summary>The active ingredients as one readable line, e.g. "Amoxicillin 500 mg".</summary>
    public string Ingredients { get; set; } = string.Empty;
}

#endregion

#region Query: search the drug database

[RequiresPermission(Permissions.InventoryView)]
public sealed record SearchDrugsQuery(string Term) : IQuery<IReadOnlyList<DrugDto>>;

internal sealed class SearchDrugsHandler(IDrugRepository drugs) : IRequestHandler<SearchDrugsQuery, IReadOnlyList<DrugDto>>
{
    public async Task<Result<IReadOnlyList<DrugDto>>> HandleAsync(SearchDrugsQuery q, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(q.Term) || q.Term.Trim().Length < 2
            ? Error.Validation("Drug.SearchTerm", "Type at least two letters to search.")
            : Result<IReadOnlyList<DrugDto>>.Success(await drugs.SearchDrugsAsync(q.Term.Trim(), ct));
}

#endregion

#region Clinical check service (shared by prescriptions and the sale)

/// <summary>
/// Gathers what the domain checker needs (drug data, the patient's allergies and current
/// medicines) and runs it. Shared so the alerts on screen while typing the prescription are the
/// same as the alerts checked again at the till.
/// </summary>
public interface IClinicalCheckService
{
    Task<IReadOnlyList<ClinicalAlert>> CheckAsync(Patient? patient, IReadOnlyList<ItemToCheck> items,
        int? exceptPrescriptionId, CancellationToken ct);
}

internal sealed class ClinicalCheckService(IDrugRepository drugs, IPatientRepository patients, IClock clock,
    BusinessRules rules) : IClinicalCheckService
{
    public async Task<IReadOnlyList<ClinicalAlert>> CheckAsync(Patient? patient, IReadOnlyList<ItemToCheck> items,
        int? exceptPrescriptionId, CancellationToken ct)
    {
        IReadOnlyList<int> current = patient is null
            ? []
            : await patients.GetCurrentMedicationItemIdsAsync(patient.Id,
                clock.UtcNow.AddDays(-rules.Clinical.CurrentMedicationDays), exceptPrescriptionId, ct);

        var profiles = (await drugs.GetClinicalProfilesAsync(items.Select(i => i.ItemId).Concat(current).Distinct(), ct))
            .ToDictionary(p => p.ItemId);
        if (profiles.Count == 0) return [];

        var ingredientCodes = profiles.Values.SelectMany(p => p.Ingredients).Select(i => i.Code).Distinct().ToList();
        IReadOnlyList<DrugInteraction> interactions = ingredientCodes.Count > 1 ? await drugs.GetInteractionsAsync(ingredientCodes, ct) : [];

        var context = new PatientContext(patient?.BirthDate,
            patient?.Allergies.Select(a => a.AllergenCode).ToList() ?? [], current);

        return ClinicalChecker.Check(items, profiles, context, interactions, clock.Today);
    }
}

#endregion

#region Query: the alerts panel (RX-03)

/// <summary>
/// Runs the checks for a patient and a list of items without saving anything, so the pharmacist
/// sees the alerts panel while choosing what to dispense. The patient is optional; without one,
/// only the checks between the items run.
/// </summary>
[RequiresPermission(Permissions.RxDispense)]
public sealed record CheckMedicinesQuery(int? PatientId, IReadOnlyList<ItemToCheck> Items) : IQuery<IReadOnlyList<ClinicalAlert>>;

internal sealed class CheckMedicinesHandler(IPatientRepository patients, IClinicalCheckService checks)
    : IRequestHandler<CheckMedicinesQuery, IReadOnlyList<ClinicalAlert>>
{
    public async Task<Result<IReadOnlyList<ClinicalAlert>>> HandleAsync(CheckMedicinesQuery q, CancellationToken ct)
    {
        if (q.Items is null || q.Items.Count == 0) return Error.Validation("Clinical.NoItems", "Add at least one item to check.");

        Patient? patient = null;
        if (q.PatientId is { } patientId && (patient = await patients.GetByIdAsync(patientId, ct)) is null)
            return Errors.NotFound("Patient", patientId);

        return Result<IReadOnlyList<ClinicalAlert>>.Success(await checks.CheckAsync(patient, q.Items, null, ct));
    }
}

#endregion
