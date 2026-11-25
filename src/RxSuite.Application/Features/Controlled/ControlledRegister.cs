using System.Globalization;
using System.Text;
using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Domain.Common;
using RxSuite.Domain.Controlled;
using RxSuite.Domain.Identity;
using RxSuite.Domain.Rules;

namespace RxSuite.Application.Features.Controlled;

// USE CASES (BRD 8.7): read and print the register (CTL-04, CTL-07), the monthly summary,
// corrections that reference the original entry (CTL-05), the signed weekly balance check (CTL-06)
// and the owner's mismatch alerts (US-CTL-3). Entries for purchases, dispenses and adjustments are
// written by those use cases (ReceiveGoods, CreateSale, AdjustStock) in the same transaction.

#region Read models

/// <summary>One line of the register as the inspector reads it.</summary>
public sealed class RegisterLineDto
{
    public long Id { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public RegisterEntryType Type { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public long DocumentId { get; set; }
    public string? BatchNumber { get; set; }
    public int QuantityIn { get; set; }
    public int QuantityOut { get; set; }
    public int Balance { get; set; }
    public string? PatientName { get; set; }
    public string? PatientNationalId { get; set; }
    public string? PrescriberName { get; set; }
    public string? PrescriberLicence { get; set; }
    public string? PrescriptionNumber { get; set; }
    public DateOnly? PrescriptionDate { get; set; }
    public int? PrescriptionImageId { get; set; }
    public string PharmacistName { get; set; } = string.Empty;
    public long? CorrectsEntryId { get; set; }
    public string? Note { get; set; }
}

/// <summary>CTL-07: one item in the monthly summary.</summary>
public sealed class RegisterMonthLineDto
{
    public int ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int OpeningBalance { get; set; }
    public int TotalIn { get; set; }
    public int TotalOut { get; set; }
    public int ClosingBalance { get; set; }
}

/// <summary>US-CTL-3: where the register, the stock and the last count stand for one controlled item.</summary>
public sealed class ControlledItemStatusDto
{
    public int ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int RegisterBalance { get; set; }
    public int StockQuantity { get; set; }
    public DateTime? LastCheckAtUtc { get; set; }
    public int? LastCheckCounted { get; set; }
    public bool LastCheckMismatch { get; set; }

    /// <summary>Filled by the use case from the business rules.</summary>
    public bool CheckOverdue { get; set; }
    public bool NeedsAttention => RegisterBalance != StockQuantity || LastCheckMismatch || CheckOverdue;
}

public sealed record ExportedFile(string FileName, string ContentType, byte[] Content);

public sealed record BalanceCheckResult(int Id, int CountedQuantity, int RegisterBalance, int StockQuantity, bool IsMismatch);

#endregion

#region Queries: the register and its export (CTL-04, CTL-07)

[RequiresPermission(Permissions.ControlledView)]
public sealed record GetControlledRegisterQuery(int BranchId, int ItemId, DateOnly From, DateOnly To)
    : IQuery<IReadOnlyList<RegisterLineDto>>;

internal sealed class GetControlledRegisterHandler(IControlledRegisterRepository register, ICurrentUser currentUser)
    : IRequestHandler<GetControlledRegisterQuery, IReadOnlyList<RegisterLineDto>>
{
    public async Task<Result<IReadOnlyList<RegisterLineDto>>> HandleAsync(GetControlledRegisterQuery q, CancellationToken ct)
    {
        if (!currentUser.CanAccessBranch(q.BranchId)) return Errors.BranchNotAllowed;
        if (q.To < q.From) return Error.Validation("Register.Dates", "The end date is before the start date.");
        return Result<IReadOnlyList<RegisterLineDto>>.Success(await register.GetEntriesAsync(q.BranchId, q.ItemId, q.From, q.To, ct));
    }
}

/// <summary>
/// CTL-07 / US-CTL-2: the register as a CSV file that opens in Excel with Arabic intact. The
/// columns follow the paper register; the official layout should be confirmed with the inspector.
/// </summary>
[RequiresPermission(Permissions.ControlledView)]
public sealed record ExportControlledRegisterQuery(int BranchId, int ItemId, DateOnly From, DateOnly To) : IQuery<ExportedFile>;

internal sealed class ExportControlledRegisterHandler(IControlledRegisterRepository register, IItemRepository items,
    ICurrentUser currentUser, IAuditTrail audit) : IRequestHandler<ExportControlledRegisterQuery, ExportedFile>
{
    private static readonly TimeZoneInfo Cairo = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");

    public async Task<Result<ExportedFile>> HandleAsync(ExportControlledRegisterQuery q, CancellationToken ct)
    {
        if (!currentUser.CanAccessBranch(q.BranchId)) return Errors.BranchNotAllowed;
        if (q.To < q.From) return Error.Validation("Register.Dates", "The end date is before the start date.");

        var item = await items.GetByIdAsync(q.ItemId, ct);
        if (item is null) return Errors.NotFound("Item", q.ItemId);

        var lines = await register.GetEntriesAsync(q.BranchId, q.ItemId, q.From, q.To, ct);

        var csv = new StringBuilder();
        csv.AppendLine(Row("Item / الصنف", item.DisplayName, item.NameAr));
        csv.AppendLine(Row("Period / الفترة", q.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            q.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        csv.AppendLine(Row("Entry", "Date / التاريخ", "Type / النوع", "Document / المستند", "Batch / التشغيلة",
            "In / وارد", "Out / منصرف", "Balance / الرصيد", "Patient / المريض", "National ID / الرقم القومي",
            "Prescriber / الطبيب", "Licence / رقم القيد", "Rx no. / رقم الروشتة", "Rx date / تاريخ الروشتة",
            "Pharmacist / الصيدلي", "Corrects / يصحح", "Note / ملاحظة"));

        foreach (var l in lines)
            csv.AppendLine(Row(l.Id.ToString(CultureInfo.InvariantCulture),
                TimeZoneInfo.ConvertTimeFromUtc(l.OccurredAtUtc, Cairo).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                l.Type.ToString(), $"{l.DocumentType} {l.DocumentId}", l.BatchNumber, Num(l.QuantityIn), Num(l.QuantityOut),
                Num(l.Balance), l.PatientName, l.PatientNationalId, l.PrescriberName, l.PrescriberLicence,
                l.PrescriptionNumber, l.PrescriptionDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                l.PharmacistName, l.CorrectsEntryId?.ToString(CultureInfo.InvariantCulture), l.Note));

        // The export holds patient data, so it is logged like any other access to the register.
        await audit.RecordAsync("Controlled", "RegisterExported", q.ItemId,
            newValue: $"{q.From:yyyy-MM-dd}..{q.To:yyyy-MM-dd}", branchId: q.BranchId, ct: ct);

        // UTF-8 with a byte-order mark: Excel then reads the Arabic correctly.
        var content = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        return new ExportedFile($"controlled-register-{item.Code}-{q.From:yyyyMMdd}-{q.To:yyyyMMdd}.csv", "text/csv", content);
    }

    private static string Num(int value) => value == 0 ? string.Empty : value.ToString(CultureInfo.InvariantCulture);

    /// <summary>One CSV row. Every value is quoted, and a value that starts like a formula is neutralised.</summary>
    private static string Row(params string?[] values) => string.Join(",", values.Select(v =>
    {
        var text = v ?? string.Empty;
        if (text.Length > 0 && "=+-@".Contains(text[0]) && !decimal.TryParse(text, CultureInfo.InvariantCulture, out _))
            text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }));
}

/// <summary>CTL-07: the monthly summary per controlled item.</summary>
[RequiresPermission(Permissions.ControlledView)]
public sealed record GetControlledMonthlySummaryQuery(int BranchId, int Year, int Month) : IQuery<IReadOnlyList<RegisterMonthLineDto>>;

internal sealed class GetControlledMonthlySummaryHandler(IControlledRegisterRepository register, ICurrentUser currentUser)
    : IRequestHandler<GetControlledMonthlySummaryQuery, IReadOnlyList<RegisterMonthLineDto>>
{
    public async Task<Result<IReadOnlyList<RegisterMonthLineDto>>> HandleAsync(GetControlledMonthlySummaryQuery q, CancellationToken ct)
    {
        if (!currentUser.CanAccessBranch(q.BranchId)) return Errors.BranchNotAllowed;
        if (q.Month is < 1 or > 12 || q.Year is < 2000 or > 2100) return Error.Validation("Register.Month", "The month is not valid.");
        return Result<IReadOnlyList<RegisterMonthLineDto>>.Success(await register.GetMonthlySummaryAsync(q.BranchId, q.Year, q.Month, ct));
    }
}

#endregion

#region Query: mismatch and overdue alerts (US-CTL-3)

[RequiresPermission(Permissions.ControlledView)]
public sealed record GetControlledStatusQuery(int BranchId, bool OnlyNeedingAttention = false)
    : IQuery<IReadOnlyList<ControlledItemStatusDto>>;

internal sealed class GetControlledStatusHandler(IControlledRegisterRepository register, ICurrentUser currentUser,
    IClock clock, BusinessRules rules) : IRequestHandler<GetControlledStatusQuery, IReadOnlyList<ControlledItemStatusDto>>
{
    public async Task<Result<IReadOnlyList<ControlledItemStatusDto>>> HandleAsync(GetControlledStatusQuery q, CancellationToken ct)
    {
        if (!currentUser.CanAccessBranch(q.BranchId)) return Errors.BranchNotAllowed;

        var dueBefore = clock.UtcNow.AddDays(-rules.Controlled.BalanceCheckIntervalDays);
        var statuses = await register.GetStatusAsync(q.BranchId, ct);
        foreach (var s in statuses) s.CheckOverdue = s.LastCheckAtUtc is null || s.LastCheckAtUtc < dueBefore;

        return statuses.Where(s => !q.OnlyNeedingAttention || s.NeedsAttention).ToList();
    }
}

#endregion

#region Command: signed balance check (CTL-06)

/// <summary>
/// The pharmacist counts the cabinet and signs with their own PIN. A mismatch needs a note and is
/// written to the audit trail for the owner (US-CTL-3).
/// </summary>
[RequiresPermission(Permissions.RxDispense)]
public sealed record RecordBalanceCheckCommand(int BranchId, int ItemId, int CountedQuantity, string Pin, string? Note)
    : ICommand<BalanceCheckResult>;

internal sealed class RecordBalanceCheckHandler(IControlledRegisterRepository register, IItemRepository items,
    IPharmacistCheck pharmacists, ICurrentUser currentUser, IClock clock, IAuditTrail audit)
    : IRequestHandler<RecordBalanceCheckCommand, BalanceCheckResult>
{
    public async Task<Result<BalanceCheckResult>> HandleAsync(RecordBalanceCheckCommand c, CancellationToken ct)
    {
        if (!currentUser.CanAccessBranch(c.BranchId)) return Errors.BranchNotAllowed;

        var pharmacist = await pharmacists.CurrentWithSignatureAsync(c.Pin, ct);
        if (pharmacist.IsFailure) return pharmacist.Error;

        var item = await items.GetByIdAsync(c.ItemId, ct);
        if (item is null) return Errors.NotFound("Item", c.ItemId);
        if (!item.IsControlled) return Error.Validation("Controlled.NotControlled", $"{item.DisplayName} is not a controlled item.");

        var check = ControlledBalanceCheck.Create(currentUser.TenantId, c.BranchId, item.Id, c.CountedQuantity,
            await register.GetBalanceAsync(c.BranchId, item.Id, ct), await register.GetStockQuantityAsync(c.BranchId, item.Id, ct),
            pharmacist.Value.Id, c.Note, clock.UtcNow);

        var id = await register.AddBalanceCheckAsync(check, ct);

        if (check.IsMismatch)
            await audit.RecordAsync("Controlled", "BalanceMismatch", item.Id,
                oldValue: $"register {check.RegisterBalance}, stock {check.StockQuantity}",
                newValue: $"counted {check.CountedQuantity}", reason: check.Note, branchId: c.BranchId, ct: ct);

        return new BalanceCheckResult(id, check.CountedQuantity, check.RegisterBalance, check.StockQuantity, check.IsMismatch);
    }
}

#endregion

#region Command: correct an entry (CTL-05)

/// <summary>
/// Adds a correction that points to the wrong entry. The wrong entry stays as it was (BR-CTL-2).
/// A quantity change here corrects the register only; physical stock is corrected with a stock adjustment.
/// </summary>
[RequiresPermission(Permissions.RxDispense)]
public sealed record CorrectRegisterEntryCommand(long EntryId, int QuantityChange, string Reason) : ICommand<Unit>;

internal sealed class CorrectRegisterEntryHandler(IControlledRegisterRepository register, IPharmacistCheck pharmacists,
    ICurrentUser currentUser, IClock clock, IAuditTrail audit) : IRequestHandler<CorrectRegisterEntryCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(CorrectRegisterEntryCommand c, CancellationToken ct)
    {
        var pharmacist = await pharmacists.CurrentAsync(ct);
        if (pharmacist.IsFailure) return pharmacist.Error;

        var original = await register.GetEntryAsync(c.EntryId, ct);
        if (original is null) return Errors.NotFound("RegisterEntry", c.EntryId);
        if (!currentUser.CanAccessBranch(original.BranchId)) return Errors.BranchNotAllowed;

        var correction = ControlledRegisterEntry.Correction(original, c.QuantityChange, c.Reason, pharmacist.Value.Id, clock.UtcNow);
        if (!await register.AddEntriesAsync([correction], ct)) return Errors.RegisterNegative;

        await audit.RecordAsync("Controlled", "RegisterCorrected", original.Id,
            newValue: c.QuantityChange.ToString(CultureInfo.InvariantCulture), reason: c.Reason.Trim(), branchId: original.BranchId, ct: ct);
        return Unit.Value;
    }
}

#endregion
