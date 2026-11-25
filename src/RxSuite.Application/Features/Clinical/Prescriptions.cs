using System.Globalization;
using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Domain.Catalog;
using RxSuite.Domain.Clinical;
using RxSuite.Domain.Common;
using RxSuite.Domain.Identity;
using RxSuite.Domain.Rules;

namespace RxSuite.Application.Features.Clinical;

// USE CASES: record a prescription with the alerts panel (RX-03, RX-04), see it with what is left
// (RX-06, AC-RX-2), the "refills due" list, cancel it, attach and view images (RX-05, BR-RX-3) and
// print dispensing labels (RX-07, US-RX-4). Dispensing itself happens at the till (CreateSale).

#region Read models

public sealed record PrescriptionLineDto(int LineNumber, int ItemId, string ItemName, UnitType Unit, int Quantity,
    int RefillsAllowed, decimal? DoseUnits, int? FrequencyPerDay, int? DurationDays, string? Instructions,
    decimal DispensedInUnit, decimal RemainingInUnit);

public sealed record PrescriptionDto(int Id, int BranchId, int PatientId, int PrescriberId, string? PrescriptionNumber,
    DateOnly PrescribedOn, string? Diagnosis, PrescriptionStatus Status, DateTime RecordedAtUtc,
    IReadOnlyList<PrescriptionLineDto> Lines, IReadOnlyList<PrescriptionImageInfo> Images);

/// <summary>What the pharmacist sees after saving: the prescription and the alerts panel (RX-03).</summary>
public sealed record PrescriptionCreated(int Id, IReadOnlyList<ClinicalAlert> Alerts);

/// <summary>A prescription with something left to collect (the "refills due" screen).</summary>
public sealed class OpenPrescriptionDto
{
    public int Id { get; set; }
    public int PatientId { get; set; }
    public string PatientName { get; set; } = string.Empty;
    public string? PatientMobile { get; set; }
    public string PrescriberName { get; set; } = string.Empty;
    public DateOnly PrescribedOn { get; set; }
    public PrescriptionStatus Status { get; set; }

    /// <summary>The medicines still owed, as one readable line.</summary>
    public string Remaining { get; set; } = string.Empty;
}

/// <summary>An image row as stored: the key to the encrypted file and its type.</summary>
public sealed class StoredPrescriptionImage
{
    public int Id { get; set; }
    public int PrescriptionId { get; set; }
    public string StorageKey { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
}

/// <summary>A decrypted image on its way to the browser.</summary>
public sealed record PrescriptionImageFile(string ContentType, byte[] Content);

/// <summary>One row behind a dispensing label: the medicine, its directions and where it was dispensed.</summary>
public sealed class DispenseLabelRow
{
    public string PatientName { get; set; } = string.Empty;
    public string ItemNameEn { get; set; } = string.Empty;
    public string ItemNameAr { get; set; } = string.Empty;
    public string DosageForm { get; set; } = string.Empty;
    public int BaseUnits { get; set; }
    public decimal? DoseUnits { get; set; }
    public int? FrequencyPerDay { get; set; }
    public int? DurationDays { get; set; }
    public string? Instructions { get; set; }
    public string? WarningEn { get; set; }
    public string? WarningAr { get; set; }
    public string PharmacyName { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public DateTime DispensedAtUtc { get; set; }
}

/// <summary>RX-07 / US-RX-4: a label ready to print, in Arabic and English.</summary>
public sealed record DispenseLabelDto(string PatientName, string MedicineEn, string MedicineAr, string DirectionsEn,
    string DirectionsAr, string? WarningEn, string? WarningAr, string Pharmacy, DateOnly Date);

internal static class PrescriptionMappings
{
    public static PrescriptionDto ToDto(this Prescription p) => new(p.Id, p.BranchId, p.PatientId, p.PrescriberId,
        p.PrescriptionNumber, p.PrescribedOn, p.Diagnosis, p.Status, p.RecordedAtUtc,
        p.Lines.Select(l => new PrescriptionLineDto(l.LineNumber, l.ItemId, l.ItemName, l.Unit, l.Quantity, l.RefillsAllowed,
            l.DoseUnits, l.FrequencyPerDay, l.DurationDays, l.Instructions,
            (decimal)l.DispensedBaseUnits / l.BaseUnitsPerUnit, l.RemainingInUnit)).ToList(),
        p.Images);
}

#endregion

#region Command: record a prescription (RX-04)

[RequiresPermission(Permissions.RxDispense)]
public sealed record CreatePrescriptionCommand(int BranchId, int PatientId, int PrescriberId, string? PrescriptionNumber,
    DateOnly PrescribedOn, string? Diagnosis, IReadOnlyList<PrescriptionLineRequest> Lines) : ICommand<PrescriptionCreated>;

internal sealed class CreatePrescriptionValidator : IValidator<CreatePrescriptionCommand>
{
    public IReadOnlyList<string> Validate(CreatePrescriptionCommand c) =>
        c.Lines is null || c.Lines.Count == 0 ? ["A prescription needs at least one medicine."] : [];
}

internal sealed class CreatePrescriptionHandler(
    IPrescriptionRepository prescriptions, IPatientRepository patients, IPrescriberRepository prescribers,
    IItemRepository items, IClinicalCheckService checks, ICurrentUser currentUser, IClock clock, IAuditTrail audit)
    : IRequestHandler<CreatePrescriptionCommand, PrescriptionCreated>
{
    public async Task<Result<PrescriptionCreated>> HandleAsync(CreatePrescriptionCommand c, CancellationToken ct)
    {
        if (!currentUser.CanAccessBranch(c.BranchId)) return Errors.BranchNotAllowed;

        var patient = await patients.GetByIdAsync(c.PatientId, ct);
        if (patient is null) return Errors.NotFound("Patient", c.PatientId);
        if (await prescribers.GetByIdAsync(c.PrescriberId, ct) is null) return Errors.NotFound("Prescriber", c.PrescriberId);

        var itemsById = (await items.GetByIdsAsync(c.Lines.Select(l => l.ItemId).Distinct(), ct)).ToDictionary(i => i.Id);
        if (c.Lines.FirstOrDefault(l => !itemsById.ContainsKey(l.ItemId)) is { } unknown)
            return Errors.NotFound("Item", unknown.ItemId);

        var prescription = Prescription.Create(currentUser.TenantId, c.BranchId, patient.Id, c.PrescriberId,
            c.PrescriptionNumber, c.PrescribedOn, c.Diagnosis, c.Lines, itemsById, currentUser.UserId, clock.UtcNow, clock.Today);

        var id = await prescriptions.AddAsync(prescription, ct);
        await audit.RecordAsync("Clinical", "PrescriptionRecorded", id, branchId: c.BranchId, ct: ct);

        // RX-03: the alerts panel is shown right away. Overrides are given when dispensing.
        var alerts = await checks.CheckAsync(patient,
            c.Lines.Select(l => new ItemToCheck(l.ItemId, l.DoseUnits, l.FrequencyPerDay)).ToList(), id, ct);

        return new PrescriptionCreated(id, alerts);
    }
}

#endregion

#region Queries: one prescription, the refills-due list

[RequiresPermission(Permissions.RxDispense)]
public sealed record GetPrescriptionQuery(int PrescriptionId) : IQuery<PrescriptionDto>;

internal sealed class GetPrescriptionHandler(IPrescriptionRepository prescriptions)
    : IRequestHandler<GetPrescriptionQuery, PrescriptionDto>
{
    public async Task<Result<PrescriptionDto>> HandleAsync(GetPrescriptionQuery q, CancellationToken ct) =>
        await prescriptions.GetByIdAsync(q.PrescriptionId, ct) is { } prescription
            ? prescription.ToDto()
            : Errors.NotFound("Prescription", q.PrescriptionId);
}

/// <summary>RX-06 "refills due": every prescription with something left to collect, or one patient's.</summary>
[RequiresPermission(Permissions.RxDispense)]
public sealed record GetOpenPrescriptionsQuery(int? PatientId) : IQuery<IReadOnlyList<OpenPrescriptionDto>>;

internal sealed class GetOpenPrescriptionsHandler(IPrescriptionRepository prescriptions)
    : IRequestHandler<GetOpenPrescriptionsQuery, IReadOnlyList<OpenPrescriptionDto>>
{
    public async Task<Result<IReadOnlyList<OpenPrescriptionDto>>> HandleAsync(GetOpenPrescriptionsQuery q, CancellationToken ct) =>
        Result<IReadOnlyList<OpenPrescriptionDto>>.Success(await prescriptions.GetOpenAsync(q.PatientId, ct));
}

#endregion

#region Command: cancel a prescription

[RequiresPermission(Permissions.RxDispense)]
public sealed record CancelPrescriptionCommand(int PrescriptionId, string Reason) : ICommand<Unit>;

internal sealed class CancelPrescriptionHandler(IPrescriptionRepository prescriptions, IAuditTrail audit)
    : IRequestHandler<CancelPrescriptionCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(CancelPrescriptionCommand c, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.Reason)) return Error.Validation("Prescription.Reason", "The reason is required.");

        var prescription = await prescriptions.GetByIdAsync(c.PrescriptionId, ct);
        if (prescription is null) return Errors.NotFound("Prescription", c.PrescriptionId);

        prescription.Cancel();
        await prescriptions.UpdateAsync(prescription, ct);
        await audit.RecordAsync("Clinical", "PrescriptionCancelled", prescription.Id, reason: c.Reason.Trim(), ct: ct);
        return Unit.Value;
    }
}

#endregion

#region Command: attach an image (RX-05, BR-RX-3)

/// <summary>A scan, webcam or phone photo of the paper prescription. Stored encrypted.</summary>
[RequiresPermission(Permissions.RxDispense)]
public sealed record AddPrescriptionImageCommand(int PrescriptionId, string ContentType, byte[] Content) : ICommand<int>;

internal sealed class AddPrescriptionImageHandler(
    IPrescriptionRepository prescriptions, IPrescriptionImageStore store, ICurrentUser currentUser, IClock clock,
    IAuditTrail audit, BusinessRules rules) : IRequestHandler<AddPrescriptionImageCommand, int>
{
    public async Task<Result<int>> HandleAsync(AddPrescriptionImageCommand c, CancellationToken ct)
    {
        if (c.Content is null || c.Content.Length == 0) return Error.Validation("Image.Empty", "The file is empty.");
        if (c.Content.Length > rules.Clinical.MaxImageBytes)
            return Error.Validation("Image.TooLarge", $"The file is larger than {rules.Clinical.MaxImageBytes / 1024 / 1024} MB.");

        // The declared type must match the file's first bytes, so a renamed file is refused.
        if (ImageSignature.Detect(c.Content) is not { } contentType || !contentType.Equals(c.ContentType, StringComparison.OrdinalIgnoreCase))
            return Error.Validation("Image.Type", "Only JPEG, PNG or PDF files are accepted.");

        var prescription = await prescriptions.GetByIdAsync(c.PrescriptionId, ct);
        if (prescription is null) return Errors.NotFound("Prescription", c.PrescriptionId);

        var key = await store.SaveAsync(currentUser.TenantId, c.Content, ct);
        var id = await prescriptions.AddImageAsync(prescription.Id, key, contentType, c.Content.Length, currentUser.UserId,
            clock.UtcNow, ct);

        await audit.RecordAsync("Clinical", "PrescriptionImageAdded", prescription.Id, newValue: id.ToString(CultureInfo.InvariantCulture), ct: ct);
        return id;
    }
}

/// <summary>Recognises the accepted file types from their first bytes.</summary>
internal static class ImageSignature
{
    public static string? Detect(ReadOnlySpan<byte> b) =>
        b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "image/jpeg"
        : b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) ? "image/png"
        : b.Length >= 5 && b[..5].SequenceEqual("%PDF-"u8) ? "application/pdf"
        : null;
}

#endregion

#region Query: view an image (BR-RX-3: every view is logged)

/// <summary>
/// Returns a decrypted image. It is a query, but it writes an audit line: BR-RX-3 asks that every
/// access to a prescription image is logged.
/// </summary>
[RequiresPermission(Permissions.RxDispense)]
public sealed record GetPrescriptionImageQuery(int PrescriptionId, int ImageId) : IQuery<PrescriptionImageFile>;

internal sealed class GetPrescriptionImageHandler(IPrescriptionRepository prescriptions, IPrescriptionImageStore store,
    ICurrentUser currentUser, IAuditTrail audit) : IRequestHandler<GetPrescriptionImageQuery, PrescriptionImageFile>
{
    public async Task<Result<PrescriptionImageFile>> HandleAsync(GetPrescriptionImageQuery q, CancellationToken ct)
    {
        var image = await prescriptions.GetImageAsync(q.PrescriptionId, q.ImageId, ct);
        if (image is null) return Errors.NotFound("PrescriptionImage", q.ImageId);

        var content = await store.ReadAsync(currentUser.TenantId, image.StorageKey, ct);
        await audit.RecordAsync("Clinical", "PrescriptionImageViewed", q.PrescriptionId,
            newValue: q.ImageId.ToString(CultureInfo.InvariantCulture), ct: ct);
        return new PrescriptionImageFile(image.ContentType, content);
    }
}

#endregion

#region Query: dispensing labels (RX-07)

[RequiresPermission(Permissions.RxDispense)]
public sealed record GetDispenseLabelsQuery(long SaleId) : IQuery<IReadOnlyList<DispenseLabelDto>>;

internal sealed class GetDispenseLabelsHandler(IDispenseRepository dispenses)
    : IRequestHandler<GetDispenseLabelsQuery, IReadOnlyList<DispenseLabelDto>>
{
    private static readonly TimeZoneInfo Cairo = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");

    public async Task<Result<IReadOnlyList<DispenseLabelDto>>> HandleAsync(GetDispenseLabelsQuery q, CancellationToken ct)
    {
        var rows = await dispenses.GetLabelRowsAsync(q.SaleId, ct);
        if (rows.Count == 0) return Error.NotFound("Label.NotFound", $"Sale {q.SaleId} has no dispensed prescription medicines.");

        return rows.Select(r => new DispenseLabelDto(r.PatientName, r.ItemNameEn,
            string.IsNullOrWhiteSpace(r.ItemNameAr) ? r.ItemNameEn : r.ItemNameAr,
            DirectionsEn(r), DirectionsAr(r), r.WarningEn, r.WarningAr,
            r.BranchName == r.PharmacyName ? r.PharmacyName : $"{r.PharmacyName} - {r.BranchName}",
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(r.DispensedAtUtc, Cairo)))).ToList();
    }

    #region Directions (structured dose first, the doctor's free text otherwise)

    private static string DirectionsEn(DispenseLabelRow r)
    {
        if (r is not { DoseUnits: { } dose, FrequencyPerDay: { } times }) return r.Instructions ?? "As directed by your doctor.";
        var text = $"Take {dose:0.##} {r.DosageForm.ToLowerInvariant()} {times} time{(times == 1 ? "" : "s")} a day";
        return (r.DurationDays is { } days ? $"{text} for {days} day{(days == 1 ? "" : "s")}." : $"{text}.")
               + (r.Instructions is { } extra ? $" {extra}" : string.Empty);
    }

    private static string DirectionsAr(DispenseLabelRow r)
    {
        if (r is not { DoseUnits: { } dose, FrequencyPerDay: { } times }) return r.Instructions ?? "حسب إرشادات الطبيب.";
        var text = $"{dose:0.##} {r.DosageForm} {times} {(times == 1 ? "مرة" : "مرات")} يوميًا";
        return (r.DurationDays is { } days ? $"{text} لمدة {days} {(days <= 10 && days > 2 ? "أيام" : "يوم")}." : $"{text}.")
               + (r.Instructions is { } extra ? $" {extra}" : string.Empty);
    }

    #endregion
}

#endregion
