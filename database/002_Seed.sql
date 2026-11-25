/* =====================================================================================
   RxSuite - seed data: plans (BRD section 12), default roles (BRD section 9) and a SAMPLE of the
   central drug database (BRD 8.6 RX-01).
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
    (N'OWNER', N'branches.manage'), (N'OWNER', N'audit.view'), (N'OWNER', N'controlled.view'),
    (N'OWNER', N'transfers.request'), (N'OWNER', N'transfers.approve'), (N'OWNER', N'transfers.ship'),
    (N'OWNER', N'transfers.receive'), (N'OWNER', N'reports.branches'),
    -- Branch manager
    (N'BRANCH_MANAGER', N'pos.sell'), (N'BRANCH_MANAGER', N'pos.override'), (N'BRANCH_MANAGER', N'pos.return'),
    (N'BRANCH_MANAGER', N'inventory.view'), (N'BRANCH_MANAGER', N'items.manage'), (N'BRANCH_MANAGER', N'prices.change'),
    (N'BRANCH_MANAGER', N'stock.adjust'), (N'BRANCH_MANAGER', N'stock.adjust.approve'),
    (N'BRANCH_MANAGER', N'purchasing.create'), (N'BRANCH_MANAGER', N'purchasing.approve'),
    (N'BRANCH_MANAGER', N'purchasing.receive'), (N'BRANCH_MANAGER', N'controlled.view'),
    (N'BRANCH_MANAGER', N'transfers.request'), (N'BRANCH_MANAGER', N'transfers.ship'), (N'BRANCH_MANAGER', N'transfers.receive'),
    (N'BRANCH_MANAGER', N'reports.branches'),
    -- Pharmacist
    (N'PHARMACIST', N'pos.sell'), (N'PHARMACIST', N'pos.return'), (N'PHARMACIST', N'rx.dispense'),
    (N'PHARMACIST', N'inventory.view'), (N'PHARMACIST', N'purchasing.create'), (N'PHARMACIST', N'purchasing.receive'),
    (N'PHARMACIST', N'controlled.view'), (N'PHARMACIST', N'stock.adjust'),
    (N'PHARMACIST', N'transfers.request'), (N'PHARMACIST', N'transfers.ship'), (N'PHARMACIST', N'transfers.receive'),
    -- Cashier
    (N'CASHIER', N'pos.sell'), (N'CASHIER', N'pos.return'), (N'CASHIER', N'inventory.view'),
    -- Inventory clerk
    (N'INVENTORY_CLERK', N'inventory.view'), (N'INVENTORY_CLERK', N'items.manage'), (N'INVENTORY_CLERK', N'stock.adjust'),
    (N'INVENTORY_CLERK', N'purchasing.create'), (N'INVENTORY_CLERK', N'purchasing.receive'),
    (N'INVENTORY_CLERK', N'transfers.request'), (N'INVENTORY_CLERK', N'transfers.ship'), (N'INVENTORY_CLERK', N'transfers.receive'),
    -- Accountant
    (N'ACCOUNTANT', N'eta.manage'), (N'ACCOUNTANT', N'inventory.view'), (N'ACCOUNTANT', N'audit.view'), (N'ACCOUNTANT', N'reports.branches'),
    -- Hospital pharmacist (its own module is not built yet)
    (N'HOSPITAL_PHARMACIST', N'rx.dispense'), (N'HOSPITAL_PHARMACIST', N'inventory.view'),
    (N'HOSPITAL_PHARMACIST', N'purchasing.receive')
) AS p (RoleCode, Permission) ON p.RoleCode = r.Code;
-- #endregion

-- #region Central drug database: SAMPLE ONLY (RX-01, RX-11)
/* A handful of generic medicines so the clinical checks can be tried end to end (AC-RX-1: a
   penicillin allergy and amoxicillin). The numbers are illustrative and NOT clinical reference data:
   production loads the full database from a licensed, professionally maintained source and keeps it
   updated centrally (RX-11). Trade names are left generic on purpose. */
INSERT INTO dbo.Ingredients (Code, Name, ClassCode, MaxDailyDoseMg, MinAgeYears) VALUES
    (N'AMOXICILLIN',    N'Amoxicillin',     N'PENICILLINS',  3000, NULL),
    (N'CLAVULANATE',    N'Clavulanic acid', N'BETALACTAMASE_INHIBITORS', NULL, NULL),
    (N'PARACETAMOL',    N'Paracetamol',     N'ANALGESICS',   4000, NULL),
    (N'IBUPROFEN',      N'Ibuprofen',       N'NSAIDS',       2400, NULL),
    (N'DICLOFENAC',     N'Diclofenac',      N'NSAIDS',        150, 14),
    (N'ASPIRIN',        N'Aspirin',         N'SALICYLATES',  4000, 16),
    (N'WARFARIN',       N'Warfarin',        N'ANTICOAGULANTS', NULL, NULL),
    (N'CLARITHROMYCIN', N'Clarithromycin',  N'MACROLIDES',   1000, NULL),
    (N'SIMVASTATIN',    N'Simvastatin',     N'STATINS',        80, NULL),
    (N'TRAMADOL',       N'Tramadol',        N'OPIOIDS',       400, 12);

INSERT INTO dbo.Drugs (TradeName, Strength, DosageForm, AtcCode, IsRxOnly, ControlSchedule, PregnancyCategory,
                       LabelWarningEn, LabelWarningAr) VALUES
    (N'Amoxicillin 500',            N'500 mg',        N'Capsule', N'J01CA04', 1, 0, N'B', N'Finish the whole course.', N'أكمل الجرعة كاملة.'),
    (N'Amoxicillin/Clavulanate 1g', N'875/125 mg',    N'Tablet',  N'J01CR02', 1, 0, N'B', N'Take with food.', N'يؤخذ مع الطعام.'),
    (N'Paracetamol 500',            N'500 mg',        N'Tablet',  N'N02BE01', 0, 0, N'B', N'Do not exceed 8 tablets a day.', N'لا تتجاوز 8 أقراص يوميًا.'),
    (N'Ibuprofen 400',              N'400 mg',        N'Tablet',  N'M01AE01', 0, 0, N'C', N'Take after food.', N'يؤخذ بعد الأكل.'),
    (N'Aspirin 75',                 N'75 mg',         N'Tablet',  N'B01AC06', 0, 0, N'D', N'Take after food.', N'يؤخذ بعد الأكل.'),
    (N'Warfarin 5',                 N'5 mg',          N'Tablet',  N'B01AA03', 1, 0, N'X', N'Regular INR tests needed.', N'يلزم تحليل سيولة دوري.'),
    (N'Clarithromycin 500',         N'500 mg',        N'Tablet',  N'J01FA09', 1, 0, N'C', NULL, NULL),
    (N'Simvastatin 20',             N'20 mg',         N'Tablet',  N'C10AA01', 1, 0, N'X', N'Take in the evening.', N'يؤخذ مساءً.'),
    (N'Tramadol 50',                N'50 mg',         N'Capsule', N'N02AX02', 1, 2, N'C', N'May cause drowsiness. Do not drive.', N'قد يسبب النعاس. لا تقد السيارة.');

INSERT INTO dbo.DrugIngredients (DrugId, IngredientCode, StrengthMgPerUnit)
SELECT d.Id, x.IngredientCode, x.Mg
FROM (VALUES
    (N'Amoxicillin 500',            N'AMOXICILLIN',    500),
    (N'Amoxicillin/Clavulanate 1g', N'AMOXICILLIN',    875),
    (N'Amoxicillin/Clavulanate 1g', N'CLAVULANATE',    125),
    (N'Paracetamol 500',            N'PARACETAMOL',    500),
    (N'Ibuprofen 400',              N'IBUPROFEN',      400),
    (N'Aspirin 75',                 N'ASPIRIN',         75),
    (N'Warfarin 5',                 N'WARFARIN',         5),
    (N'Clarithromycin 500',         N'CLARITHROMYCIN', 500),
    (N'Simvastatin 20',             N'SIMVASTATIN',     20),
    (N'Tramadol 50',                N'TRAMADOL',        50)
) AS x (TradeName, IngredientCode, Mg)
JOIN dbo.Drugs d ON d.TradeName = x.TradeName;

INSERT INTO dbo.DrugInteractions (IngredientA, IngredientB, Severity, Description, Recommendation) VALUES
    (N'ASPIRIN',        N'WARFARIN',    3, N'Higher risk of bleeding.', N'Avoid unless prescribed together; watch for bleeding.'),
    (N'IBUPROFEN',      N'WARFARIN',    3, N'Higher risk of bleeding.', N'Prefer paracetamol for pain.'),
    (N'DICLOFENAC',     N'WARFARIN',    3, N'Higher risk of bleeding.', N'Prefer paracetamol for pain.'),
    (N'ASPIRIN',        N'IBUPROFEN',   2, N'Ibuprofen can reduce the heart-protective effect of low-dose aspirin.', N'Take aspirin at least 30 minutes before ibuprofen.'),
    (N'CLARITHROMYCIN', N'SIMVASTATIN', 4, N'Greatly raises simvastatin levels: risk of muscle damage.', N'Do not combine. Pause simvastatin or choose another antibiotic.');
-- #endregion
