/* =====================================================================================
   RxSuite - seed data: plans (BRD section 12) and default roles (BRD section 9).
   ===================================================================================== */

-- #region Plans
-- The BRD leaves prices as "EGP [TBC]": set MonthlyPricePerBranch before taking payments.
INSERT INTO dbo.Plans (Code, NameEn, NameAr, MonthlyPricePerBranch, MaxBranches, MaxUsersPerBranch) VALUES
    (N'BASIC',      N'Basic',      N'الأساسية',  0, 1,    3),
    (N'PRO',        N'Pro',        N'المتقدمة',  0, NULL, 10),
    (N'ENTERPRISE', N'Enterprise', N'المؤسسات',  0, NULL, NULL);
-- #endregion

-- #region Roles (USR-01)
INSERT INTO dbo.Roles (Code, NameEn, NameAr, MaxDiscountPercent, HasAllBranches) VALUES
    (N'OWNER',               N'Owner',               N'المالك',            100, 1),
    (N'BRANCH_MANAGER',      N'Branch manager',      N'مدير الفرع',        100, 0),
    (N'PHARMACIST',          N'Pharmacist',          N'صيدلي',              10, 0),
    (N'CASHIER',             N'Cashier',             N'كاشير',               5, 0),
    (N'INVENTORY_CLERK',     N'Inventory clerk',     N'أمين المخزن',         0, 0),
    (N'ACCOUNTANT',          N'Accountant',          N'محاسب',               0, 1),
    (N'HOSPITAL_PHARMACIST', N'Hospital pharmacist', N'صيدلي المستشفى',      0, 0),
    (N'NURSE',               N'Nurse',               N'ممرض',                0, 0),
    (N'DRIVER',              N'Driver',              N'مندوب التوصيل',       0, 0);
-- #endregion

-- #region Role permissions (default matrix of BRD section 9; codes match Domain/Identity Permissions)
INSERT INTO dbo.RolePermissions (RoleId, Permission)
SELECT r.Id, p.Permission
FROM dbo.Roles r
JOIN (VALUES
    -- Owner / HQ: everything
    (N'OWNER', N'pos.sell'), (N'OWNER', N'pos.override'), (N'OWNER', N'pos.return'),
    (N'OWNER', N'inventory.view'), (N'OWNER', N'items.manage'), (N'OWNER', N'prices.change'),
    (N'OWNER', N'prices.above-official'), (N'OWNER', N'stock.adjust'), (N'OWNER', N'stock.adjust.approve'),
    (N'OWNER', N'purchasing.create'), (N'OWNER', N'purchasing.approve'), (N'OWNER', N'purchasing.receive'),
    (N'OWNER', N'eta.manage'), (N'OWNER', N'users.manage'), (N'OWNER', N'subscription.manage'),
    (N'OWNER', N'branches.manage'), (N'OWNER', N'audit.view'),
    -- Branch manager
    (N'BRANCH_MANAGER', N'pos.sell'), (N'BRANCH_MANAGER', N'pos.override'), (N'BRANCH_MANAGER', N'pos.return'),
    (N'BRANCH_MANAGER', N'inventory.view'), (N'BRANCH_MANAGER', N'items.manage'), (N'BRANCH_MANAGER', N'prices.change'),
    (N'BRANCH_MANAGER', N'stock.adjust'), (N'BRANCH_MANAGER', N'stock.adjust.approve'),
    (N'BRANCH_MANAGER', N'purchasing.create'), (N'BRANCH_MANAGER', N'purchasing.approve'),
    (N'BRANCH_MANAGER', N'purchasing.receive'),
    -- Pharmacist
    (N'PHARMACIST', N'pos.sell'), (N'PHARMACIST', N'pos.return'), (N'PHARMACIST', N'rx.dispense'),
    (N'PHARMACIST', N'inventory.view'), (N'PHARMACIST', N'purchasing.create'), (N'PHARMACIST', N'purchasing.receive'),
    -- Cashier
    (N'CASHIER', N'pos.sell'), (N'CASHIER', N'pos.return'), (N'CASHIER', N'inventory.view'),
    -- Inventory clerk
    (N'INVENTORY_CLERK', N'inventory.view'), (N'INVENTORY_CLERK', N'items.manage'), (N'INVENTORY_CLERK', N'stock.adjust'),
    (N'INVENTORY_CLERK', N'purchasing.create'), (N'INVENTORY_CLERK', N'purchasing.receive'),
    -- Accountant
    (N'ACCOUNTANT', N'eta.manage'), (N'ACCOUNTANT', N'inventory.view'), (N'ACCOUNTANT', N'audit.view'),
    -- Hospital pharmacist (its own module is not built yet)
    (N'HOSPITAL_PHARMACIST', N'rx.dispense'), (N'HOSPITAL_PHARMACIST', N'inventory.view'),
    (N'HOSPITAL_PHARMACIST', N'purchasing.receive')
) AS p (RoleCode, Permission) ON p.RoleCode = r.Code;
-- #endregion
