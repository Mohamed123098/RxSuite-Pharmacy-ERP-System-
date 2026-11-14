using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Domain.Common;
using RxSuite.Domain.Identity;
using RxSuite.Domain.Rules;
using RxSuite.Domain.Sales;

namespace RxSuite.Application.Features.Sales;

// USE CASES: the cash shift (POS-16; Figure 11): open with a float, pay-in / pay-out with a
// reason, X-report during the shift, close with the counted cash.

#region DTO

/// <summary>X-report (while open) and Z-report (after closing) of a shift.</summary>
public sealed record ShiftSummaryDto(int ShiftId, int BranchId, int CashierId, ShiftStatus Status, DateTime OpenedAtUtc,
    DateTime? ClosedAtUtc, decimal OpeningFloat, decimal CashSales, decimal CashRefunds, decimal PayIns,
    decimal PayOuts, decimal ExpectedCash, decimal? CountedCash, decimal? Variance);

internal static class ShiftMappings
{
    public static ShiftSummaryDto ToSummary(this Shift shift, ShiftCashTotals totals) => new(
        shift.Id, shift.BranchId, shift.CashierId, shift.Status, shift.OpenedAtUtc, shift.ClosedAtUtc,
        shift.OpeningFloat, totals.CashSales, totals.CashRefunds, totals.PayIns, totals.PayOuts,
        shift.ExpectedCashFor(totals), shift.CountedCash, shift.Variance);
}

#endregion

#region Command: open a shift

[RequiresPermission(Permissions.PosSell)]
public sealed record OpenShiftCommand(int BranchId, string TerminalCode, decimal OpeningFloat) : ICommand<int>;

internal sealed class OpenShiftHandler(IShiftRepository shifts, ICurrentUser currentUser, IClock clock)
    : IRequestHandler<OpenShiftCommand, int>
{
    public async Task<Result<int>> HandleAsync(OpenShiftCommand c, CancellationToken ct)
    {
        if (!currentUser.CanAccessBranch(c.BranchId)) return Errors.BranchNotAllowed;
        if (await shifts.GetOpenAsync(c.BranchId, currentUser.UserId, ct) is not null) return Errors.ShiftAlreadyOpen;

        var shift = Shift.Open(currentUser.TenantId, c.BranchId, currentUser.UserId, c.TerminalCode, c.OpeningFloat, clock.UtcNow);
        return await shifts.AddAsync(shift, ct);
    }
}

#endregion

#region Command: pay-in / pay-out

[RequiresPermission(Permissions.PosSell)]
public sealed record RecordCashMovementCommand(int ShiftId, CashMovementType Type, decimal Amount, string Reason) : ICommand<Unit>;

internal sealed class RecordCashMovementHandler(IShiftRepository shifts, ICurrentUser currentUser, IAuditTrail audit)
    : IRequestHandler<RecordCashMovementCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(RecordCashMovementCommand c, CancellationToken ct)
    {
        if (c.Amount <= 0 || string.IsNullOrWhiteSpace(c.Reason) || !Enum.IsDefined(c.Type))
            return Error.Validation("Cash.Invalid", "A pay-in or pay-out needs a positive amount and a reason.");

        var shift = await shifts.GetByIdAsync(c.ShiftId, ct);
        if (shift is null) return Errors.NotFound("Shift", c.ShiftId);
        if (!shift.IsOpen || shift.CashierId != currentUser.UserId) return Errors.ShiftNotOpen;

        await shifts.AddCashMovementAsync(shift.Id, c.Type, c.Amount, c.Reason.Trim(), ct);
        await audit.RecordAsync("POS", c.Type.ToString(), shift.Id, newValue: c.Amount.ToString("0.00"),
            reason: c.Reason, branchId: shift.BranchId, ct: ct);
        return Unit.Value;
    }
}

#endregion

#region Command: close a shift

[RequiresPermission(Permissions.PosSell)]
public sealed record CloseShiftCommand(int ShiftId, decimal CountedCash, string? Note, ManagerOverride? Approval)
    : ICommand<ShiftSummaryDto>;

internal sealed class CloseShiftHandler(
    IShiftRepository shifts, ICurrentUser currentUser, IOverrideAuthorizer overrides, IAuditTrail audit,
    IClock clock, BusinessRules rules) : IRequestHandler<CloseShiftCommand, ShiftSummaryDto>
{
    public async Task<Result<ShiftSummaryDto>> HandleAsync(CloseShiftCommand c, CancellationToken ct)
    {
        var shift = await shifts.GetByIdAsync(c.ShiftId, ct);
        if (shift is null) return Errors.NotFound("Shift", c.ShiftId);
        if (!shift.IsOpen || shift.CashierId != currentUser.UserId) return Errors.ShiftNotOpen;

        var totals = await shifts.GetCashTotalsAsync(shift.Id, ct);

        // Figure 11 "Above tolerance?": a large variance needs a manager's sign-off.
        int? approvedBy = null;
        if (Math.Abs(shift.VarianceFor(c.CountedCash, totals)) > rules.Pos.ShiftVarianceTolerance)
        {
            var approval = await overrides.AuthorizeAsync(c.Approval, Permissions.PosOverride, ct);
            if (approval.IsFailure) return approval.Error;
            approvedBy = approval.Value.Id;
        }

        shift.Close(c.CountedCash, totals, rules.Pos.ShiftVarianceTolerance, c.Note, approvedBy, clock.UtcNow);
        await shifts.CloseAsync(shift, ct);

        if (shift.Variance != 0)
            await audit.RecordAsync("POS", "ShiftVariance", shift.Id, shift.ExpectedCash?.ToString("0.00"),
                shift.CountedCash?.ToString("0.00"), c.Note, shift.BranchId, ct);

        return shift.ToSummary(totals);
    }
}

#endregion

#region Query: X / Z report

[RequiresPermission(Permissions.PosSell)]
public sealed record GetShiftSummaryQuery(int ShiftId) : IQuery<ShiftSummaryDto>;

internal sealed class GetShiftSummaryHandler(IShiftRepository shifts, ICurrentUser currentUser)
    : IRequestHandler<GetShiftSummaryQuery, ShiftSummaryDto>
{
    public async Task<Result<ShiftSummaryDto>> HandleAsync(GetShiftSummaryQuery q, CancellationToken ct)
    {
        var shift = await shifts.GetByIdAsync(q.ShiftId, ct);
        if (shift is null) return Errors.NotFound("Shift", q.ShiftId);
        if (!currentUser.CanAccessBranch(shift.BranchId)) return Errors.BranchNotAllowed;

        return shift.ToSummary(await shifts.GetCashTotalsAsync(shift.Id, ct));
    }
}

#endregion
