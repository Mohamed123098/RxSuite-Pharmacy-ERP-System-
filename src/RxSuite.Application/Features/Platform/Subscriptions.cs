using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Domain.Common;
using RxSuite.Domain.Identity;
using RxSuite.Domain.Platform;
using RxSuite.Domain.Rules;

namespace RxSuite.Application.Features.Platform;

// USE CASES for the subscription life cycle (SUB-06, SUB-07, SUB-11, SUB-12; BR-SUB-2..4).

#region Read models

/// <summary>The billing page (SUB-11): plan, status, renewal and how much of the plan is used.</summary>
public sealed record SubscriptionDto(string PlanCode, string PlanName, TenantStatus Status, bool IsTrial,
    BillingCycle BillingCycle, DateOnly RenewalDate, decimal Price, int MaxBranches, int BranchesUsed,
    int? MaxUsers, int UsersUsed);

/// <summary>What the nightly job needs to know about one tenant's subscription.</summary>
public sealed class SubscriptionState
{
    public int TenantId { get; set; }
    public bool IsTrial { get; set; }
    public DateOnly RenewalDate { get; set; }
    public TenantStatus Status { get; set; }
}

#endregion

#region Query: billing page

[RequiresPermission(Permissions.SubscriptionManage)]
public sealed record GetSubscriptionQuery : IQuery<SubscriptionDto>;

internal sealed class GetSubscriptionHandler(
    IPlatformRepository platform, IUserRepository users, ICurrentUser currentUser, IClock clock, BusinessRules rules)
    : IRequestHandler<GetSubscriptionQuery, SubscriptionDto>
{
    public async Task<Result<SubscriptionDto>> HandleAsync(GetSubscriptionQuery request, CancellationToken ct)
    {
        var subscription = await platform.GetSubscriptionAsync(currentUser.TenantId, ct);
        if (subscription is null) return Errors.SubscriptionNotFound;

        var plan = (await platform.GetPlansAsync(ct)).First(p => p.Id == subscription.PlanId);
        var branches = await platform.GetBranchesAsync(currentUser.TenantId, ct);

        return new SubscriptionDto(plan.Code, plan.NameEn,
            subscription.StatusOn(clock.Today, rules.Subscription.GraceDays), subscription.IsTrial,
            subscription.BillingCycle, subscription.RenewalDate, subscription.Price,
            subscription.MaxBranches(rules.Subscription),
            branches.Count(b => b is { IsActive: true, Type: BranchType.Branch }),
            subscription.MaxUsers(plan, rules.Subscription), await users.CountActiveAsync(ct));
    }
}

#endregion

#region Command: a payment was confirmed by the gateway

/// <summary>
/// Sent by the payment webhook after Paymob or Fawry confirms a payment (SUB-06). Only the
/// gateway reference is kept, never card data.
/// </summary>
[NoUserRequired]
public sealed record RecordSubscriptionPaymentCommand(int TenantId, string PlanCode, BillingCycle BillingCycle,
    int BranchCount, decimal AmountPaid, string Method, string GatewayReference) : ICommand<DateOnly>;

internal sealed class RecordSubscriptionPaymentHandler(IPlatformRepository platform, IAuditTrail audit, IClock clock, BusinessRules rules)
    : IRequestHandler<RecordSubscriptionPaymentCommand, DateOnly>
{
    public async Task<Result<DateOnly>> HandleAsync(RecordSubscriptionPaymentCommand c, CancellationToken ct)
    {
        var subscription = await platform.GetSubscriptionAsync(c.TenantId, ct);
        if (subscription is null) return Errors.SubscriptionNotFound;

        var plan = (await platform.GetPlansAsync(ct)).FirstOrDefault(p => p.Code == c.PlanCode && p.IsActive);
        if (plan is null) return Errors.PlanNotFound(c.PlanCode);

        // The amount must match the price list: plan x branches x cycle (BR-SUB-2, BR-SUB-3).
        var expected = plan.PriceFor(c.BillingCycle, c.BranchCount, rules.Subscription.YearlyPriceMultiplier);
        if (c.AmountPaid != expected)
            return Error.Validation("Subscription.AmountMismatch",
                $"The payment of {c.AmountPaid:0.00} EGP does not match the price of {expected:0.00} EGP.");

        subscription.Renew(plan, c.BillingCycle, c.BranchCount, clock.Today, rules.Subscription.YearlyPriceMultiplier);

        await platform.UpdateSubscriptionAsync(subscription, ct);
        await platform.AddSubscriptionPaymentAsync(c.TenantId, c.AmountPaid, c.Method, c.GatewayReference, ct);
        await platform.SetTenantStatusAsync(c.TenantId, TenantStatus.Active, ct);
        await audit.RecordForAsync(c.TenantId, null, "Platform", "SubscriptionPayment", subscription.Id,
            $"{plan.Code} {c.BillingCycle} x{c.BranchCount} = {c.AmountPaid:0.00}", ct);

        return subscription.RenewalDate;
    }
}

#endregion

#region Command: nightly status refresh

/// <summary>
/// Run by the nightly job. Moves each tenant to the status its subscription dates call for:
/// trial or paid period over -> Grace; grace days over -> ReadOnly (BR-SUB-4, AC-SUB-2).
/// Returns how many tenants changed status.
/// </summary>
[NoUserRequired]
public sealed record RefreshSubscriptionStatusesCommand : ICommand<int>;

internal sealed class RefreshSubscriptionStatusesHandler(IPlatformRepository platform, IClock clock, BusinessRules rules)
    : IRequestHandler<RefreshSubscriptionStatusesCommand, int>
{
    public async Task<Result<int>> HandleAsync(RefreshSubscriptionStatusesCommand request, CancellationToken ct)
    {
        var changed = 0;

        foreach (var state in await platform.GetSubscriptionStatesAsync(ct))
        {
            if (state.Status == TenantStatus.Cancelled) continue;

            var due = Subscription.StatusOn(state.IsTrial, state.RenewalDate, clock.Today, rules.Subscription.GraceDays);
            if (due == state.Status) continue;

            await platform.SetTenantStatusAsync(state.TenantId, due, ct);
            changed++;
        }

        return changed;
    }
}

#endregion
