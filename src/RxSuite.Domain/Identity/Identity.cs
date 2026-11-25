using RxSuite.Domain.Common;
using RxSuite.Domain.Rules;

namespace RxSuite.Domain.Identity;

// BRD 8.2 "Users, security and audit" and section 9 "Roles and permissions matrix".

#region Permissions

/// <summary>
/// The permission codes checked by the application. A role is a named set of these (USR-02).
/// The default set per role (BRD section 9) is seeded in database/002_Seed.sql.
/// </summary>
public static class Permissions
{
    public const string PosSell = "pos.sell";
    public const string PosOverride = "pos.override";            // discount above limit, shift variance (USR-07)
    public const string PosReturn = "pos.return";
    public const string RxDispense = "rx.dispense";              // pharmacist-only actions (BR-USR-4)
    public const string InventoryView = "inventory.view";
    public const string ItemsManage = "items.manage";
    public const string PricesChange = "prices.change";
    public const string PricesAboveOfficial = "prices.above-official"; // BR-POS-2 override
    public const string StockAdjust = "stock.adjust";
    public const string StockAdjustApprove = "stock.adjust.approve";
    public const string PurchasingCreate = "purchasing.create";
    public const string PurchasingApprove = "purchasing.approve";
    public const string PurchasingReceive = "purchasing.receive";
    public const string EtaManage = "eta.manage";
    public const string UsersManage = "users.manage";
    public const string SubscriptionManage = "subscription.manage";
    public const string BranchesManage = "branches.manage";
    public const string AuditView = "audit.view";
    public const string ControlledView = "controlled.view";      // CTL-07: read and print the register
    public const string TransfersRequest = "transfers.request";  // US-MBR-2: ask for stock
    public const string TransfersApprove = "transfers.approve";  // MBR-03 approve, MBR-04 direct transfers (HQ)
    public const string TransfersShip = "transfers.ship";        // MBR-03 pick and ship at the source
    public const string TransfersReceive = "transfers.receive";  // MBR-03 receive at the destination
    public const string BranchReports = "reports.branches";      // MBR-05/06/08: cross-branch views
}

#endregion

#region Role

/// <summary>A named set of permissions plus the limits that go with it (BRD key data "Role").</summary>
public sealed class Role : Entity<int>
{
    public string Code { get; private set; } = string.Empty;
    public string NameEn { get; private set; } = string.Empty;
    public string NameAr { get; private set; } = string.Empty;

    /// <summary>POS-07: the largest discount this role may give without a manager override.</summary>
    public decimal MaxDiscountPercent { get; private set; }

    /// <summary>USR-03: roles such as Owner work on every branch without being assigned to each.</summary>
    public bool HasAllBranches { get; private set; }

    public IReadOnlyCollection<string> Permissions { get; private set; } = [];

    private Role() { }

    public static Role Create(int id, string code, string nameEn, decimal maxDiscountPercent, bool hasAllBranches,
        IEnumerable<string> permissions) =>
        new()
        {
            Id = id, Code = code, NameEn = nameEn, NameAr = nameEn, MaxDiscountPercent = maxDiscountPercent,
            HasAllBranches = hasAllBranches, Permissions = permissions.ToHashSet()
        };

    /// <summary>Attaches the permission list read from the database.</summary>
    public void LoadPermissions(IEnumerable<string> permissions) => Permissions = permissions.ToHashSet();

    public bool Has(string permission) => Permissions.Contains(permission);
}

#endregion

#region User

/// <summary>
/// AGGREGATE ROOT. A person who can log in. Owns the login rules: lockout, disabling and the
/// pharmacist licence check.
/// </summary>
public sealed class User : TenantEntity<int>
{
    #region State

    public string FullName { get; private set; } = string.Empty;
    public string Mobile { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;

    /// <summary>USR-07: hashed 4-6 digit PIN used to approve another user's action.</summary>
    public string? PinHash { get; private set; }
    public int RoleId { get; private set; }
    public bool IsPharmacist { get; private set; }
    public string? LicenceNumber { get; private set; }
    public DateOnly? LicenceExpiry { get; private set; }
    public bool IsActive { get; private set; }
    public int FailedLoginCount { get; private set; }
    public DateTime? LockedUntilUtc { get; private set; }
    public DateTime? LastLoginUtc { get; private set; }

    private User() { }

    #endregion

    #region Factory

    public static User Create(int tenantId, string fullName, string mobile, string email, string passwordHash,
        string? pinHash, int roleId, bool isPharmacist, string? licenceNumber, DateOnly? licenceExpiry)
    {
        if (string.IsNullOrWhiteSpace(fullName)) throw new DomainException("The user's name is required.");
        if (string.IsNullOrWhiteSpace(mobile) && string.IsNullOrWhiteSpace(email))
            throw new DomainException("A mobile number or an e-mail is required to log in.");
        if (isPharmacist && (string.IsNullOrWhiteSpace(licenceNumber) || licenceExpiry is null))
            throw new DomainException("A pharmacist needs a licence number and its expiry date."); // USR-10

        return new User
        {
            TenantId = tenantId, FullName = fullName.Trim(), Mobile = mobile.Trim(),
            Email = email.Trim().ToLowerInvariant(), PasswordHash = passwordHash, PinHash = pinHash,
            RoleId = roleId, IsPharmacist = isPharmacist, LicenceNumber = licenceNumber?.Trim(),
            LicenceExpiry = licenceExpiry, IsActive = true
        };
    }

    #endregion

    #region Login rules (USR-04)

    public bool IsLockedOut(DateTime nowUtc) => LockedUntilUtc is { } until && until > nowUtc;

    /// <summary>Counts a wrong password; the account locks after the allowed number of attempts.</summary>
    public void RegisterFailedLogin(DateTime nowUtc, SecurityRules rules)
    {
        FailedLoginCount++;
        if (FailedLoginCount < rules.MaxFailedLogins) return;

        LockedUntilUtc = nowUtc.AddMinutes(rules.LockoutMinutes);
        FailedLoginCount = 0;
    }

    public void RegisterSuccessfulLogin(DateTime nowUtc)
    {
        FailedLoginCount = 0;
        LockedUntilUtc = null;
        LastLoginUtc = nowUtc;
    }

    #endregion

    #region Behaviour

    /// <summary>BR-USR-3: users are disabled, never deleted, so their history stays readable.</summary>
    public void Disable() => IsActive = false;

    /// <summary>BR-USR-4: pharmacist-only actions need the flag and a licence that has not expired.</summary>
    public bool CanActAsPharmacist(DateOnly today) =>
        IsActive && IsPharmacist && LicenceExpiry is { } expiry && expiry >= today;

    #endregion
}

#endregion

#region Audit entry

/// <summary>
/// One line of the audit trail (USR-11, BRD key data "Audit entry"). Immutable by design:
/// the table has no update or delete procedure and a trigger rejects both (USR-12, NFR-13).
/// </summary>
public sealed record AuditEntry(
    DateTime TimestampUtc, int TenantId, int? UserId, int? BranchId, string Module, string Action,
    string? RecordId, string? OldValue, string? NewValue, string? Reason);

#endregion
