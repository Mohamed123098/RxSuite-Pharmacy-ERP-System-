using RxSuite.Domain.Common;
using RxSuite.Domain.Rules;

namespace RxSuite.Domain.Platform;

// BRD 8.1 "Tenant, subscription and billing". Enum values are stored as TINYINT: never renumber.

#region Enums

public enum CustomerType : byte { Pharmacy = 1, Chain = 2, Hospital = 3 }

/// <summary>Tenant life cycle (BRD key data: trial / active / grace / read-only / cancelled).</summary>
public enum TenantStatus : byte { Trial = 1, Active = 2, Grace = 3, ReadOnly = 4, Cancelled = 5 }

public enum BillingCycle : byte { Monthly = 1, Yearly = 2 }

public enum BranchType : byte { Branch = 1, Warehouse = 2 }

#endregion

#region Plan

/// <summary>A subscription plan (Basic, Pro, Enterprise). Priced per active branch (BR-SUB-2).</summary>
public sealed class Plan : Entity<int>
{
    public string Code { get; private set; } = string.Empty;
    public string NameEn { get; private set; } = string.Empty;
    public string NameAr { get; private set; } = string.Empty;
    public decimal MonthlyPricePerBranch { get; private set; }

    /// <summary>Null = unlimited.</summary>
    public int? MaxBranches { get; private set; }
    public int? MaxUsersPerBranch { get; private set; }
    public bool IsActive { get; private set; }

    private Plan() { }

    public static Plan Create(int id, string code, string nameEn, string nameAr, decimal monthlyPricePerBranch,
        int? maxBranches, int? maxUsersPerBranch) =>
        new()
        {
            Id = id, Code = code, NameEn = nameEn, NameAr = nameAr, MonthlyPricePerBranch = monthlyPricePerBranch,
            MaxBranches = maxBranches, MaxUsersPerBranch = maxUsersPerBranch, IsActive = true
        };

    /// <summary>Price of one billing period. BR-SUB-3: a year costs <paramref name="yearlyMultiplier"/> months.</summary>
    public decimal PriceFor(BillingCycle cycle, int branchCount, int yearlyMultiplier) =>
        MonthlyPricePerBranch * branchCount * (cycle == BillingCycle.Yearly ? yearlyMultiplier : 1);
}

#endregion

#region Tenant

/// <summary>
/// AGGREGATE ROOT. One legal customer (BR-SUB-1) that owns branches, users and all business data.
/// </summary>
public sealed class Tenant : Entity<int>
{
    public string LegalNameEn { get; private set; } = string.Empty;
    public string LegalNameAr { get; private set; } = string.Empty;
    public CustomerType CustomerType { get; private set; }
    public string TaxRegistrationNumber { get; private set; } = string.Empty;
    public string CommercialRegisterNumber { get; private set; } = string.Empty;
    public string LicenceNumber { get; private set; } = string.Empty;
    public string Governorate { get; private set; } = string.Empty;
    public string City { get; private set; } = string.Empty;
    public string Address { get; private set; } = string.Empty;
    public TenantStatus Status { get; private set; }

    private Tenant() { }

    /// <summary>SUB-01: creates the tenant from the public sign-up form. It starts in trial.</summary>
    public static Tenant Register(string legalNameEn, string legalNameAr, CustomerType customerType,
        string taxRegistrationNumber, string commercialRegisterNumber, string licenceNumber,
        string governorate, string city, string address)
    {
        if (string.IsNullOrWhiteSpace(legalNameEn) && string.IsNullOrWhiteSpace(legalNameAr))
            throw new DomainException("The pharmacy name is required.");
        if (string.IsNullOrWhiteSpace(licenceNumber))
            throw new DomainException("The pharmacy licence number is required.");

        return new Tenant
        {
            LegalNameEn = legalNameEn.Trim(), LegalNameAr = legalNameAr.Trim(), CustomerType = customerType,
            TaxRegistrationNumber = taxRegistrationNumber.Trim(),
            CommercialRegisterNumber = commercialRegisterNumber.Trim(), LicenceNumber = licenceNumber.Trim(),
            Governorate = governorate.Trim(), City = city.Trim(), Address = address.Trim(),
            Status = TenantStatus.Trial
        };
    }

    /// <summary>BR-SUB-4: a read-only or cancelled tenant can view and export but cannot sell or change data.</summary>
    public bool CanWrite => Status is TenantStatus.Trial or TenantStatus.Active or TenantStatus.Grace;

    public void SetStatus(TenantStatus status) => Status = status;
}

#endregion

#region Subscription

/// <summary>The tenant's current subscription: which plan, how many branches, and until when it is paid.</summary>
public sealed class Subscription : TenantEntity<int>
{
    public int PlanId { get; private set; }
    public BillingCycle BillingCycle { get; private set; }
    public int BranchCount { get; private set; }
    public bool IsTrial { get; private set; }
    public DateOnly StartDate { get; private set; }

    /// <summary>Last day covered. For a trial this is the day the trial ends.</summary>
    public DateOnly RenewalDate { get; private set; }
    public decimal Price { get; private set; }
    public bool AutoRenew { get; private set; }

    private Subscription() { }

    /// <summary>SUB-04: a free trial on the trial plan, no payment card needed.</summary>
    public static Subscription StartTrial(int tenantId, int trialPlanId, DateOnly today, SubscriptionRules rules) =>
        new()
        {
            TenantId = tenantId, PlanId = trialPlanId, BillingCycle = BillingCycle.Monthly, BranchCount = 1,
            IsTrial = true, StartDate = today, RenewalDate = today.AddDays(rules.TrialDays), Price = 0m,
            AutoRenew = true
        };

    /// <summary>
    /// The status the tenant should have on <paramref name="today"/> (BR-SUB-4):
    /// paid or in trial -> Trial/Active; up to the grace days after -> Grace; later -> ReadOnly.
    /// </summary>
    public TenantStatus StatusOn(DateOnly today, int graceDays) => StatusOn(IsTrial, RenewalDate, today, graceDays);

    /// <summary>The same rule for callers that only hold the two dates (the nightly job).</summary>
    public static TenantStatus StatusOn(bool isTrial, DateOnly renewalDate, DateOnly today, int graceDays)
    {
        if (today <= renewalDate) return isTrial ? TenantStatus.Trial : TenantStatus.Active;
        return today <= renewalDate.AddDays(graceDays) ? TenantStatus.Grace : TenantStatus.ReadOnly;
    }

    /// <summary>
    /// A successful payment: the trial ends and the paid period is extended by one cycle.
    /// A payment made while still paid-up extends from the renewal date, a late one from today.
    /// </summary>
    public void Renew(Plan plan, BillingCycle cycle, int branchCount, DateOnly paidOn, int yearlyMultiplier)
    {
        if (branchCount < 1) throw new DomainException("A subscription covers at least one branch.");
        if (plan.MaxBranches is { } max && branchCount > max)
            throw new DomainException($"The {plan.NameEn} plan allows at most {max} branch(es).");

        var from = !IsTrial && RenewalDate > paidOn ? RenewalDate : paidOn;

        PlanId = plan.Id;
        BillingCycle = cycle;
        BranchCount = branchCount;
        IsTrial = false;
        Price = plan.PriceFor(cycle, branchCount, yearlyMultiplier);
        RenewalDate = cycle == BillingCycle.Yearly ? from.AddYears(1) : from.AddMonths(1);
    }

    #region Plan limits (SUB-12)

    /// <summary>Branches the tenant may run: the trial limit, or the number it pays for (BR-SUB-2).</summary>
    public int MaxBranches(SubscriptionRules rules) => IsTrial ? rules.TrialMaxBranches : BranchCount;

    public int? MaxUsers(Plan plan, SubscriptionRules rules) =>
        IsTrial ? rules.TrialMaxUsers : plan.MaxUsersPerBranch * BranchCount;

    #endregion
}

#endregion

#region Branch

/// <summary>A selling branch or a warehouse of a tenant (MBR-01). Stock is tracked per branch.</summary>
public sealed class Branch : TenantEntity<int>
{
    public string NameEn { get; private set; } = string.Empty;
    public string NameAr { get; private set; } = string.Empty;
    public BranchType Type { get; private set; }
    public string Address { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }

    private Branch() { }

    public static Branch Create(int tenantId, string nameEn, string nameAr, BranchType type, string address)
    {
        if (string.IsNullOrWhiteSpace(nameEn) && string.IsNullOrWhiteSpace(nameAr))
            throw new DomainException("The branch name is required.");

        return new Branch
        {
            TenantId = tenantId, NameEn = nameEn.Trim(), NameAr = nameAr.Trim(), Type = type,
            Address = address.Trim(), IsActive = true
        };
    }
}

#endregion
