using RxSuite.Domain.Common;

namespace RxSuite.Application.Common;

#region Paging

/// <summary>One page of a list plus the total number of rows.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int PageNumber, int PageSize);

#endregion

#region Error catalogue

/// <summary>
/// The expected failures, in one place, so clients get stable codes and handlers do not invent
/// different messages for the same situation.
/// </summary>
public static class Errors
{
    #region Access

    public static readonly Error NotLoggedIn = Error.Unauthorized("Auth.Required", "Log in to continue.");
    public static readonly Error InvalidLogin = Error.Unauthorized("Auth.Invalid", "Wrong login or password.");
    public static readonly Error AccountLocked =
        Error.Unauthorized("Auth.Locked", "Too many wrong attempts. The account is locked for a few minutes.");
    public static Error MissingPermission(string permission) =>
        Error.Forbidden("Auth.Forbidden", $"You do not have the permission '{permission}'.");
    public static readonly Error BranchNotAllowed =
        Error.Forbidden("Auth.Branch", "You are not assigned to this branch.");
    public static readonly Error TenantReadOnly = Error.Forbidden("Tenant.ReadOnly",
        "The subscription has expired. The account is read-only until it is renewed.");
    public static Error OverrideRefused(string why) => Error.Forbidden("Override.Refused", why);

    #endregion

    #region Platform

    public static readonly Error LoginTaken =
        Error.Conflict("User.LoginTaken", "This mobile number or e-mail is already registered.");
    public static Error PlanNotFound(string code) => Error.NotFound("Plan.NotFound", $"Plan '{code}' was not found.");
    public static Error PlanLimit(string what, int limit) => Error.Conflict("Plan.LimitReached",
        $"Your plan allows {limit} {what}. Upgrade the plan to add more.");
    public static readonly Error SubscriptionNotFound =
        Error.NotFound("Subscription.NotFound", "The tenant has no subscription.");

    #endregion

    #region Generic

    public static Error NotFound(string what, object id) => Error.NotFound($"{what}.NotFound", $"{what} {id} was not found.");

    #endregion

    #region Operations

    public static readonly Error ShiftNotOpen =
        Error.Conflict("Shift.NotOpen", "Open a shift on this branch before selling.");
    public static readonly Error ShiftAlreadyOpen =
        Error.Conflict("Shift.AlreadyOpen", "You already have an open shift on this branch.");
    public static readonly Error StockChanged = Error.Conflict("Stock.Changed",
        "Stock changed while the operation was running. Nothing was saved; please try again.");

    #endregion

    #region Prescriptions and controlled drugs (BRD 8.6, 8.7)

    public static readonly Error PharmacistOnly = Error.Forbidden("Controlled.PharmacistOnly",
        "Only a logged-in pharmacist with a valid licence can do this (CTL-02).");
    public static Error PrescriptionRequired(string itemName) => Error.Conflict("Rx.PrescriptionRequired",
        $"{itemName} needs a recorded prescription before it can be dispensed.");
    public static Error OverrideRequired(IEnumerable<string> alertCodes) => Error.Conflict("Clinical.OverrideRequired",
        $"These alerts need a pharmacist override with a reason: {string.Join(", ", alertCodes)}.");
    public static readonly Error RegisterNegative = Error.Conflict("Controlled.RegisterNegative",
        "The controlled register balance would go below zero. Record the opening balance with a stock adjustment first.");
    public static readonly Error ControlledReturn = Error.Conflict("Controlled.NoCounterReturn",
        "Controlled medicines cannot be returned at the counter. Record them with a stock adjustment by a pharmacist.");

    #endregion
}

#endregion
