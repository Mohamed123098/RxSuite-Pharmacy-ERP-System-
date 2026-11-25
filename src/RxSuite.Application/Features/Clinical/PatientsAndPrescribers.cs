using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Domain.Clinical;
using RxSuite.Domain.Common;
using RxSuite.Domain.Identity;

namespace RxSuite.Application.Features.Clinical;

// USE CASES: patients with their allergies and medication history (US-RX-3, RX-08), and the
// prescriber directory (RX-04, RX-09).

#region Read models

public sealed class PatientSummaryDto
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? NationalId { get; set; }
    public string? Mobile { get; set; }
    public DateOnly? BirthDate { get; set; }
}

/// <summary>RX-08: one dispensed medicine in the patient's history, from any branch.</summary>
public sealed class MedicationHistoryLine
{
    public DateTime DispensedAtUtc { get; set; }
    public string BranchName { get; set; } = string.Empty;
    public int ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int BaseUnits { get; set; }
    public int? PrescriptionId { get; set; }
    public string? PrescriberName { get; set; }
    public string PharmacistName { get; set; } = string.Empty;
    public string? CounsellingNote { get; set; }
}

/// <summary>The "patient medication profile" screen: who, allergies, history and what is still to collect.</summary>
public sealed record PatientProfileDto(int Id, string FullName, string? NationalId, string? Mobile, DateOnly? BirthDate,
    IReadOnlyList<PatientAllergy> Allergies, IReadOnlyList<MedicationHistoryLine> History,
    IReadOnlyList<OpenPrescriptionDto> OpenPrescriptions);

public sealed class PrescriberDto
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Specialty { get; set; }
    public string? LicenceNumber { get; set; }
    public string? Clinic { get; set; }

    /// <summary>RX-09: how many prescriptions of this doctor the pharmacy has recorded.</summary>
    public int PrescriptionCount { get; set; }
}

#endregion

#region Command: register a patient

[RequiresPermission(Permissions.RxDispense)]
public sealed record CreatePatientCommand(string FullName, string? NationalId, string? Mobile, DateOnly? BirthDate,
    IReadOnlyList<PatientAllergy>? Allergies) : ICommand<int>;

internal sealed class CreatePatientHandler(IPatientRepository patients, ICurrentUser currentUser, IClock clock, IAuditTrail audit)
    : IRequestHandler<CreatePatientCommand, int>
{
    public async Task<Result<int>> HandleAsync(CreatePatientCommand c, CancellationToken ct)
    {
        var patient = Patient.Create(currentUser.TenantId, c.FullName, c.NationalId, c.Mobile, c.BirthDate,
            c.Allergies ?? [], clock.Today);

        if (patient.NationalId is { } nationalId && await patients.NationalIdExistsAsync(nationalId, ct))
            return Error.Conflict("Patient.Exists", "A patient with this national ID is already registered.");

        var id = await patients.AddAsync(patient, ct);
        await audit.RecordAsync("Clinical", "PatientCreated", id, ct: ct);
        return id;
    }
}

#endregion

#region Command: record an allergy (US-RX-3)

[RequiresPermission(Permissions.RxDispense)]
public sealed record AddPatientAllergyCommand(int PatientId, string AllergenCode, string? Note) : ICommand<Unit>;

internal sealed class AddPatientAllergyHandler(IPatientRepository patients, IAuditTrail audit)
    : IRequestHandler<AddPatientAllergyCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(AddPatientAllergyCommand c, CancellationToken ct)
    {
        var patient = await patients.GetByIdAsync(c.PatientId, ct);
        if (patient is null) return Errors.NotFound("Patient", c.PatientId);

        // Recording an allergy that is already there is not an error; nothing changes.
        if (!patient.AddAllergy(c.AllergenCode, c.Note)) return Unit.Value;

        await patients.AddAllergyAsync(patient.Id, patient.Allergies[^1], ct);
        await audit.RecordAsync("Clinical", "AllergyRecorded", patient.Id, newValue: patient.Allergies[^1].AllergenCode, ct: ct);
        return Unit.Value;
    }
}

#endregion

#region Queries: find a patient, open the profile

[RequiresPermission(Permissions.RxDispense)]
public sealed record SearchPatientsQuery(string Term) : IQuery<IReadOnlyList<PatientSummaryDto>>;

internal sealed class SearchPatientsHandler(IPatientRepository patients)
    : IRequestHandler<SearchPatientsQuery, IReadOnlyList<PatientSummaryDto>>
{
    public async Task<Result<IReadOnlyList<PatientSummaryDto>>> HandleAsync(SearchPatientsQuery q, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(q.Term) || q.Term.Trim().Length < 2
            ? Error.Validation("Patient.SearchTerm", "Type at least two characters: a name, a mobile or a national ID.")
            : Result<IReadOnlyList<PatientSummaryDto>>.Success(await patients.SearchAsync(q.Term.Trim(), ct));
}

/// <summary>US-RX-3 / RX-08: allergies, history from every branch, and prescriptions still to collect.</summary>
[RequiresPermission(Permissions.RxDispense)]
public sealed record GetPatientProfileQuery(int PatientId) : IQuery<PatientProfileDto>;

internal sealed class GetPatientProfileHandler(IPatientRepository patients, IPrescriptionRepository prescriptions)
    : IRequestHandler<GetPatientProfileQuery, PatientProfileDto>
{
    public async Task<Result<PatientProfileDto>> HandleAsync(GetPatientProfileQuery q, CancellationToken ct)
    {
        var patient = await patients.GetByIdAsync(q.PatientId, ct);
        if (patient is null) return Errors.NotFound("Patient", q.PatientId);

        return new PatientProfileDto(patient.Id, patient.FullName, patient.NationalId, patient.Mobile, patient.BirthDate,
            patient.Allergies, await patients.GetMedicationHistoryAsync(patient.Id, ct),
            await prescriptions.GetOpenAsync(patient.Id, ct));
    }
}

#endregion

#region Prescribers (RX-04, RX-09)

[RequiresPermission(Permissions.RxDispense)]
public sealed record CreatePrescriberCommand(string FullName, string? Specialty, string? LicenceNumber, string? Clinic)
    : ICommand<int>;

internal sealed class CreatePrescriberHandler(IPrescriberRepository prescribers, ICurrentUser currentUser)
    : IRequestHandler<CreatePrescriberCommand, int>
{
    public async Task<Result<int>> HandleAsync(CreatePrescriberCommand c, CancellationToken ct) =>
        await prescribers.AddAsync(Prescriber.Create(currentUser.TenantId, c.FullName, c.Specialty, c.LicenceNumber, c.Clinic), ct);
}

/// <summary>RX-09: directory search; the most frequent prescribers come first.</summary>
[RequiresPermission(Permissions.RxDispense)]
public sealed record SearchPrescribersQuery(string? Term) : IQuery<IReadOnlyList<PrescriberDto>>;

internal sealed class SearchPrescribersHandler(IPrescriberRepository prescribers)
    : IRequestHandler<SearchPrescribersQuery, IReadOnlyList<PrescriberDto>>
{
    public async Task<Result<IReadOnlyList<PrescriberDto>>> HandleAsync(SearchPrescribersQuery q, CancellationToken ct) =>
        Result<IReadOnlyList<PrescriberDto>>.Success(await prescribers.SearchAsync(q.Term?.Trim() ?? string.Empty, ct));
}

#endregion
