namespace RxSuite.Domain.Rules;

/// <summary>
/// Every configurable number the BRD names, in one place. Bound from the "BusinessRules" section
/// of appsettings.json (OPTIONS pattern), so changing a rule is a configuration change.
/// The BRD wants several of these per tenant; this version keeps them per installation.
/// </summary>
public sealed class BusinessRules
{
    public const string SectionName = "BusinessRules";

    public SubscriptionRules Subscription { get; init; } = new();
    public SecurityRules Security { get; init; } = new();
    public InventoryRules Inventory { get; init; } = new();
    public PurchasingRules Purchasing { get; init; } = new();
    public PosRules Pos { get; init; } = new();
}

#region Subscription (BRD 8.1)

public sealed class SubscriptionRules
{
    /// <summary>SUB-04: 14-day free trial.</summary>
    public int TrialDays { get; init; } = 14;
    public int TrialMaxBranches { get; init; } = 2;
    public int TrialMaxUsers { get; init; } = 5;

    /// <summary>Code of the plan whose features the trial runs on (SUB-04: Pro).</summary>
    public string TrialPlanCode { get; init; } = "PRO";

    /// <summary>BR-SUB-4: days after a failed renewal before the tenant becomes read-only.</summary>
    public int GraceDays { get; init; } = 7;

    /// <summary>BR-SUB-3: yearly billing = 10 x monthly price.</summary>
    public int YearlyPriceMultiplier { get; init; } = 10;
}

#endregion

#region Security (BRD 8.2)

public sealed class SecurityRules
{
    /// <summary>USR-04: minimum password length; letters and numbers are always required.</summary>
    public int MinPasswordLength { get; init; } = 8;
    public int MaxFailedLogins { get; init; } = 5;
    public int LockoutMinutes { get; init; } = 15;
}

#endregion

#region Inventory (BRD 8.4)

public sealed class InventoryRules
{
    /// <summary>INV-07: batches expiring within this many days appear on the near-expiry board.</summary>
    public int NearExpiryDays { get; init; } = 90;

    /// <summary>BR-INV-3: a stock reduction worth more than this needs manager approval.</summary>
    public decimal AdjustmentApprovalLimit { get; init; } = 1000m;
}

#endregion

#region Purchasing (BRD 8.5)

public sealed class PurchasingRules
{
    /// <summary>PUR-04 / AC-PUR-1: orders above this value wait for approval.</summary>
    public decimal OrderApprovalLimit { get; init; } = 20000m;

    /// <summary>PUR-07: receiving an item that expires sooner than this needs a reason.</summary>
    public int MinimumShelfLifeMonths { get; init; } = 6;
}

#endregion

#region Point of sale (BRD 8.3)

public sealed class PosRules
{
    /// <summary>BR-POS-3: strip and unit prices are rounded to this step.</summary>
    public decimal UnitPriceRounding { get; init; } = 0.25m;

    /// <summary>Returns flow: days after the sale in which a return is accepted.</summary>
    public int ReturnPolicyDays { get; init; } = 14;

    /// <summary>Shift flow: a cash variance above this needs a manager's sign-off.</summary>
    public decimal ShiftVarianceTolerance { get; init; } = 50m;

    /// <summary>BR-POS-6: from this receipt total the buyer's national ID and name are required.</summary>
    public decimal BuyerIdentificationThreshold { get; init; } = 150000m;
}

#endregion
