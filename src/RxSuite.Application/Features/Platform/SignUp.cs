using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Application.Features.Identity;
using RxSuite.Domain.Common;
using RxSuite.Domain.Identity;
using RxSuite.Domain.Platform;
using RxSuite.Domain.Rules;

namespace RxSuite.Application.Features.Platform;

// USE CASE (SUB-01, SUB-04, AC-SUB-1; Figure 1): a pharmacy signs up and gets a trial tenant
// with its first branch and its owner user, ready to log in.
// Not built yet: mobile OTP check (SUB-01), document upload (SUB-02), welcome message.

#region Command and result

[NoUserRequired]
public sealed record SignUpCommand(
    string OwnerName, string Mobile, string Email, string Password,
    string PharmacyNameEn, string PharmacyNameAr, CustomerType CustomerType,
    string Governorate, string City, string Address,
    string LicenceNumber, string CommercialRegisterNumber, string TaxRegistrationNumber) : ICommand<SignUpResult>;

public sealed record SignUpResult(int TenantId, int OwnerUserId, int BranchId, DateOnly TrialEndsOn);

#endregion

#region Validator

internal sealed class SignUpValidator(BusinessRules rules) : IValidator<SignUpCommand>
{
    public IReadOnlyList<string> Validate(SignUpCommand c)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(c.OwnerName)) problems.Add("Owner name is required.");
        if (string.IsNullOrWhiteSpace(c.Mobile)) problems.Add("Mobile number is required.");
        if (string.IsNullOrWhiteSpace(c.Email) || !c.Email.Contains('@')) problems.Add("A valid e-mail is required.");
        if (!Enum.IsDefined(c.CustomerType)) problems.Add("Customer type is not valid.");
        problems.AddRange(PasswordPolicy.Check(c.Password, rules.Security));

        return problems;
    }
}

#endregion

#region Handler

internal sealed class SignUpHandler(
    IPlatformRepository platform, IUserRepository users, ISecretHasher hasher, IAuditTrail audit,
    IClock clock, BusinessRules rules) : IRequestHandler<SignUpCommand, SignUpResult>
{
    public const string OwnerRoleCode = "OWNER";

    public async Task<Result<SignUpResult>> HandleAsync(SignUpCommand c, CancellationToken ct)
    {
        if (await users.LoginExistsAsync(c.Mobile.Trim(), c.Email.Trim().ToLowerInvariant(), ct))
            return Errors.LoginTaken;

        var trialPlan = (await platform.GetPlansAsync(ct))
            .FirstOrDefault(p => p.Code == rules.Subscription.TrialPlanCode);
        if (trialPlan is null) return Errors.PlanNotFound(rules.Subscription.TrialPlanCode);

        var ownerRole = await users.GetRoleByCodeAsync(OwnerRoleCode, ct);
        if (ownerRole is null) return Errors.NotFound("Role", OwnerRoleCode);

        // 1. The tenant, in trial.
        var tenant = Tenant.Register(c.PharmacyNameEn, c.PharmacyNameAr, c.CustomerType, c.TaxRegistrationNumber,
            c.CommercialRegisterNumber, c.LicenceNumber, c.Governorate, c.City, c.Address);
        var tenantId = await platform.AddTenantAsync(tenant, ct);

        // 2. Its trial subscription (SUB-04).
        var subscription = Subscription.StartTrial(tenantId, trialPlan.Id, clock.Today, rules.Subscription);
        await platform.AddSubscriptionAsync(subscription, ct);

        // 3. Its first branch.
        var branch = Branch.Create(tenantId, "Main branch", "الفرع الرئيسي", BranchType.Branch, c.Address);
        var branchId = await platform.AddBranchAsync(branch, ct);

        // 4. The owner, who can log in immediately (AC-SUB-1).
        var owner = User.Create(tenantId, c.OwnerName, c.Mobile, c.Email, hasher.Hash(c.Password),
            pinHash: null, ownerRole.Id, isPharmacist: false, licenceNumber: null, licenceExpiry: null);
        var ownerId = await users.AddAsync(owner, [branchId], ct);

        await audit.RecordForAsync(tenantId, ownerId, "Platform", "SignUp", tenantId, tenant.LegalNameEn, ct);

        return new SignUpResult(tenantId, ownerId, branchId, subscription.RenewalDate);
    }
}

#endregion
