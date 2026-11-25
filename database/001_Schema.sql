/* =====================================================================================
   RxSuite - database schema (SQL Server 2019+)
   Run order: 001_Schema.sql -> 002_Seed.sql -> every file in /StoredProcedures
   - Every tenant-owned table carries TenantId (NFR-05); every procedure filters on it.
   - Enum columns are TINYINT and match the C# enums in the Domain project.
   - Stock quantities are in the smallest unit (BR-INV-1).
   ===================================================================================== */

-- #region Platform (BRD 8.1)
CREATE TABLE dbo.Plans
(
    Id                    INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Plans PRIMARY KEY,
    Code                  NVARCHAR(20)  NOT NULL CONSTRAINT UQ_Plans_Code UNIQUE,
    NameEn                NVARCHAR(50)  NOT NULL,
    NameAr                NVARCHAR(50)  NOT NULL,
    MonthlyPricePerBranch DECIMAL(18,2) NOT NULL,
    MaxBranches           INT           NULL,   -- NULL = unlimited
    MaxUsersPerBranch     INT           NULL,   -- NULL = unlimited
    IsActive              BIT           NOT NULL CONSTRAINT DF_Plans_IsActive DEFAULT (1)
);

CREATE TABLE dbo.Tenants
(
    Id                       INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Tenants PRIMARY KEY,
    LegalNameEn              NVARCHAR(200) NOT NULL,
    LegalNameAr              NVARCHAR(200) NOT NULL,
    CustomerType             TINYINT       NOT NULL,  -- 1 Pharmacy, 2 Chain, 3 Hospital
    TaxRegistrationNumber    NVARCHAR(30)  NOT NULL,
    CommercialRegisterNumber NVARCHAR(30)  NOT NULL,
    LicenceNumber            NVARCHAR(30)  NOT NULL,
    Governorate              NVARCHAR(60)  NOT NULL,
    City                     NVARCHAR(60)  NOT NULL,
    Address                  NVARCHAR(300) NOT NULL,
    Status                   TINYINT       NOT NULL,  -- 1 Trial, 2 Active, 3 Grace, 4 ReadOnly, 5 Cancelled
    CreatedAtUtc             DATETIME2(0)  NOT NULL CONSTRAINT DF_Tenants_CreatedAtUtc DEFAULT (SYSUTCDATETIME())
);

CREATE TABLE dbo.Subscriptions
(
    Id           INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Subscriptions PRIMARY KEY,
    TenantId     INT           NOT NULL CONSTRAINT FK_Subscriptions_Tenants REFERENCES dbo.Tenants (Id)
                                        CONSTRAINT UQ_Subscriptions_TenantId UNIQUE,  -- one current subscription per tenant
    PlanId       INT           NOT NULL CONSTRAINT FK_Subscriptions_Plans REFERENCES dbo.Plans (Id),
    BillingCycle TINYINT       NOT NULL,  -- 1 Monthly, 2 Yearly
    BranchCount  INT           NOT NULL,
    IsTrial      BIT           NOT NULL,
    StartDate    DATE          NOT NULL,
    RenewalDate  DATE          NOT NULL,
    Price        DECIMAL(18,2) NOT NULL,
    AutoRenew    BIT           NOT NULL
);

CREATE TABLE dbo.SubscriptionPayments
(
    Id               INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SubscriptionPayments PRIMARY KEY,
    TenantId         INT           NOT NULL CONSTRAINT FK_SubscriptionPayments_Tenants REFERENCES dbo.Tenants (Id),
    Amount           DECIMAL(18,2) NOT NULL,
    Method           NVARCHAR(30)  NOT NULL,
    GatewayReference NVARCHAR(100) NOT NULL,  -- SUB-06: a gateway token/reference, never card data
    PaidAtUtc        DATETIME2(0)  NOT NULL CONSTRAINT DF_SubscriptionPayments_PaidAtUtc DEFAULT (SYSUTCDATETIME())
);

CREATE TABLE dbo.Branches
(
    Id       INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Branches PRIMARY KEY,
    TenantId INT           NOT NULL CONSTRAINT FK_Branches_Tenants REFERENCES dbo.Tenants (Id),
    NameEn   NVARCHAR(100) NOT NULL,
    NameAr   NVARCHAR(100) NOT NULL,
    Type     TINYINT       NOT NULL,  -- 1 Branch, 2 Warehouse
    Address  NVARCHAR(300) NOT NULL,
    IsActive BIT           NOT NULL CONSTRAINT DF_Branches_IsActive DEFAULT (1)
);
CREATE INDEX IX_Branches_TenantId ON dbo.Branches (TenantId);
-- #endregion

-- #region Identity and audit (BRD 8.2)
CREATE TABLE dbo.Roles
(
    Id                 INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Roles PRIMARY KEY,
    Code               NVARCHAR(30) NOT NULL CONSTRAINT UQ_Roles_Code UNIQUE,
    NameEn             NVARCHAR(50) NOT NULL,
    NameAr             NVARCHAR(50) NOT NULL,
    MaxDiscountPercent DECIMAL(5,2) NOT NULL,
    HasAllBranches     BIT          NOT NULL
);

CREATE TABLE dbo.RolePermissions
(
    RoleId     INT          NOT NULL CONSTRAINT FK_RolePermissions_Roles REFERENCES dbo.Roles (Id),
    Permission NVARCHAR(50) NOT NULL,
    CONSTRAINT PK_RolePermissions PRIMARY KEY (RoleId, Permission)
);

CREATE TABLE dbo.Users
(
    Id               INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
    TenantId         INT           NOT NULL CONSTRAINT FK_Users_Tenants REFERENCES dbo.Tenants (Id),
    FullName         NVARCHAR(150) NOT NULL,
    Mobile           NVARCHAR(20)  NOT NULL,
    Email            NVARCHAR(150) NOT NULL,
    PasswordHash     NVARCHAR(200) NOT NULL,
    PinHash          NVARCHAR(200) NULL,
    RoleId           INT           NOT NULL CONSTRAINT FK_Users_Roles REFERENCES dbo.Roles (Id),
    IsPharmacist     BIT           NOT NULL,
    LicenceNumber    NVARCHAR(30)  NULL,
    LicenceExpiry    DATE          NULL,
    IsActive         BIT           NOT NULL CONSTRAINT DF_Users_IsActive DEFAULT (1),
    FailedLoginCount INT           NOT NULL CONSTRAINT DF_Users_FailedLoginCount DEFAULT (0),
    LockedUntilUtc   DATETIME2(0)  NULL,
    LastLoginUtc     DATETIME2(0)  NULL,
    CreatedAtUtc     DATETIME2(0)  NOT NULL CONSTRAINT DF_Users_CreatedAtUtc DEFAULT (SYSUTCDATETIME())
);
-- A login (mobile or e-mail) identifies one user across the whole platform.
CREATE UNIQUE INDEX UQ_Users_Email  ON dbo.Users (Email)  WHERE Email  <> N'';
CREATE UNIQUE INDEX UQ_Users_Mobile ON dbo.Users (Mobile) WHERE Mobile <> N'';
CREATE INDEX IX_Users_TenantId ON dbo.Users (TenantId);

CREATE TABLE dbo.UserBranches
(
    UserId   INT NOT NULL CONSTRAINT FK_UserBranches_Users REFERENCES dbo.Users (Id),
    BranchId INT NOT NULL CONSTRAINT FK_UserBranches_Branches REFERENCES dbo.Branches (Id),
    TenantId INT NOT NULL,
    CONSTRAINT PK_UserBranches PRIMARY KEY (UserId, BranchId)
);

CREATE TABLE dbo.AuditLog
(
    Id           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditLog PRIMARY KEY,
    TenantId     INT           NOT NULL,
    TimestampUtc DATETIME2(0)  NOT NULL,
    UserId       INT           NULL,
    BranchId     INT           NULL,
    Module       NVARCHAR(30)  NOT NULL,
    Action       NVARCHAR(50)  NOT NULL,
    RecordId     NVARCHAR(50)  NULL,
    OldValue     NVARCHAR(MAX) NULL,
    NewValue     NVARCHAR(MAX) NULL,
    Reason       NVARCHAR(500) NULL
);
CREATE INDEX IX_AuditLog_Tenant_Time ON dbo.AuditLog (TenantId, TimestampUtc DESC);
GO

-- USR-12 / NFR-13: audit entries cannot be changed or deleted by anyone.
CREATE TRIGGER dbo.TR_AuditLog_Immutable ON dbo.AuditLog
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'Audit entries are immutable: they cannot be updated or deleted.', 1;
END
GO
-- #endregion

-- #region Central drug database (BRD 8.6 RX-01, RX-11)
-- Shared by every tenant and updated centrally, so these tables carry no TenantId.
-- Doses and limits are per ingredient; strengths are per smallest unit (one tablet, one ml).
CREATE TABLE dbo.Ingredients
(
    Code           NVARCHAR(50)  NOT NULL CONSTRAINT PK_Ingredients PRIMARY KEY,   -- e.g. AMOXICILLIN
    Name           NVARCHAR(150) NOT NULL,
    ClassCode      NVARCHAR(50)  NULL,     -- e.g. PENICILLINS: class allergies and duplicate therapy
    MaxDailyDoseMg DECIMAL(12,3) NULL,
    MinAgeYears    INT           NULL
);

CREATE TABLE dbo.Drugs
(
    Id                    INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Drugs PRIMARY KEY,
    TradeName             NVARCHAR(200) NOT NULL,
    Strength              NVARCHAR(50)  NOT NULL,
    DosageForm            NVARCHAR(50)  NOT NULL,
    AtcCode               NVARCHAR(10)  NULL,
    Manufacturer          NVARCHAR(150) NULL,
    EdaRegistrationNumber NVARCHAR(50)  NULL,
    OfficialPrice         DECIMAL(18,2) NULL,
    IsRxOnly              BIT           NOT NULL,
    ControlSchedule       TINYINT       NOT NULL CONSTRAINT DF_Drugs_ControlSchedule DEFAULT (0),  -- 0 None, 1 Narcotic, 2 Psychotropic
    PregnancyCategory     NVARCHAR(5)   NULL,
    LabelWarningEn        NVARCHAR(300) NULL,
    LabelWarningAr        NVARCHAR(300) NULL,
    UpdatedAtUtc          DATETIME2(0)  NOT NULL CONSTRAINT DF_Drugs_UpdatedAtUtc DEFAULT (SYSUTCDATETIME())
);
CREATE INDEX IX_Drugs_TradeName ON dbo.Drugs (TradeName);

CREATE TABLE dbo.DrugIngredients
(
    DrugId            INT           NOT NULL CONSTRAINT FK_DrugIngredients_Drugs REFERENCES dbo.Drugs (Id),
    IngredientCode    NVARCHAR(50)  NOT NULL CONSTRAINT FK_DrugIngredients_Ingredients REFERENCES dbo.Ingredients (Code),
    StrengthMgPerUnit DECIMAL(12,3) NULL,
    CONSTRAINT PK_DrugIngredients PRIMARY KEY (DrugId, IngredientCode)
);

-- RX-02: each pair is stored once, with the codes in alphabetical order.
CREATE TABLE dbo.DrugInteractions
(
    IngredientA    NVARCHAR(50)  NOT NULL CONSTRAINT FK_DrugInteractions_A REFERENCES dbo.Ingredients (Code),
    IngredientB    NVARCHAR(50)  NOT NULL CONSTRAINT FK_DrugInteractions_B REFERENCES dbo.Ingredients (Code),
    Severity       TINYINT       NOT NULL CONSTRAINT CK_DrugInteractions_Severity CHECK (Severity BETWEEN 1 AND 4),  -- 1 Minor .. 4 Contraindicated
    Description    NVARCHAR(500) NOT NULL,
    Recommendation NVARCHAR(300) NULL,
    CONSTRAINT PK_DrugInteractions PRIMARY KEY (IngredientA, IngredientB),
    CONSTRAINT CK_DrugInteractions_Order CHECK (IngredientA < IngredientB)
);
CREATE INDEX IX_DrugInteractions_B ON dbo.DrugInteractions (IngredientB);
-- #endregion

-- #region Catalog and inventory (BRD 8.4)
CREATE TABLE dbo.Items
(
    Id             INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Items PRIMARY KEY,
    TenantId       INT           NOT NULL CONSTRAINT FK_Items_Tenants REFERENCES dbo.Tenants (Id),
    Code           NVARCHAR(30)  NOT NULL,
    NameEn         NVARCHAR(200) NOT NULL,
    NameAr         NVARCHAR(200) NOT NULL,
    ScientificName NVARCHAR(200) NOT NULL,
    Strength       NVARCHAR(50)  NOT NULL,
    DosageForm     NVARCHAR(50)  NOT NULL,
    StripsPerBox   INT           NOT NULL CONSTRAINT CK_Items_StripsPerBox CHECK (StripsPerBox >= 1),
    UnitsPerStrip  INT           NOT NULL CONSTRAINT CK_Items_UnitsPerStrip CHECK (UnitsPerStrip >= 1),
    OfficialPrice  DECIMAL(18,2) NULL,      -- EDA price of a box
    SellingPrice   DECIMAL(18,2) NOT NULL CONSTRAINT CK_Items_SellingPrice CHECK (SellingPrice > 0),
    VatRate        DECIMAL(5,4)  NOT NULL,  -- 0 = exempt, 0.14 = 14%
    EtaItemCode    NVARCHAR(50)  NULL,      -- ACC-02
    IsRxOnly       BIT           NOT NULL,
    ControlSchedule TINYINT      NOT NULL CONSTRAINT DF_Items_ControlSchedule DEFAULT (0),  -- CTL-01: 0 None, 1 Narcotic, 2 Psychotropic
    DrugId         INT           NULL CONSTRAINT FK_Items_Drugs REFERENCES dbo.Drugs (Id),  -- RX-01
    ControlledLimitPerPrescription INT NULL,  -- CTL-08, smallest units
    ControlledLimitPerPatient      INT NULL,  -- CTL-08, smallest units per limit window
    ReorderLevel   INT           NOT NULL,
    IsActive       BIT           NOT NULL CONSTRAINT DF_Items_IsActive DEFAULT (1),
    CONSTRAINT UQ_Items_Tenant_Code UNIQUE (TenantId, Code)
);

CREATE TABLE dbo.ItemBarcodes
(
    Id       INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ItemBarcodes PRIMARY KEY,
    TenantId INT          NOT NULL,
    ItemId   INT          NOT NULL CONSTRAINT FK_ItemBarcodes_Items REFERENCES dbo.Items (Id),
    Barcode  NVARCHAR(50) NOT NULL,
    CONSTRAINT UQ_ItemBarcodes_Tenant_Barcode UNIQUE (TenantId, Barcode)   -- INV-02: many barcodes per item
);

CREATE TABLE dbo.Batches
(
    Id            INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Batches PRIMARY KEY,
    TenantId      INT           NOT NULL,
    BranchId      INT           NOT NULL CONSTRAINT FK_Batches_Branches REFERENCES dbo.Branches (Id),
    ItemId        INT           NOT NULL CONSTRAINT FK_Batches_Items REFERENCES dbo.Items (Id),
    BatchNumber   NVARCHAR(50)  NOT NULL,
    ExpiryDate    DATE          NOT NULL,
    Quantity      INT           NOT NULL CONSTRAINT CK_Batches_Quantity CHECK (Quantity >= 0),  -- final guard against negative stock
    UnitCost      DECIMAL(18,6) NOT NULL,  -- cost of one smallest unit
    Status        TINYINT       NOT NULL CONSTRAINT DF_Batches_Status DEFAULT (1),  -- 1 Available, 2 Quarantine, 3 Recalled
    ReceivedAtUtc DATETIME2(0)  NOT NULL CONSTRAINT DF_Batches_ReceivedAtUtc DEFAULT (SYSUTCDATETIME()),
    -- BR-INV-2: a batch is item + batch number + expiry (per branch)
    CONSTRAINT UQ_Batches_Key UNIQUE (TenantId, BranchId, ItemId, BatchNumber, ExpiryDate)
);
CREATE INDEX IX_Batches_Expiry ON dbo.Batches (Status, ExpiryDate);

CREATE TABLE dbo.StockMovements
(
    Id           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockMovements PRIMARY KEY,
    TenantId     INT           NOT NULL,
    BranchId     INT           NOT NULL,
    BatchId      INT           NOT NULL CONSTRAINT FK_StockMovements_Batches REFERENCES dbo.Batches (Id),
    ItemId       INT           NOT NULL,
    Quantity     INT           NOT NULL,  -- signed: + in, - out
    Type         TINYINT       NOT NULL,  -- 1 Purchase, 2 Sale, 3 SaleReturn, 4 Adjustment, 5 TransferOut, 6 TransferIn
    DocumentType NVARCHAR(30)  NOT NULL,
    DocumentId   BIGINT        NOT NULL,
    Reason       NVARCHAR(300) NULL,
    UserId       INT           NULL,
    CreatedAtUtc DATETIME2(0)  NOT NULL CONSTRAINT DF_StockMovements_CreatedAtUtc DEFAULT (SYSUTCDATETIME())
);
CREATE INDEX IX_StockMovements_Card ON dbo.StockMovements (TenantId, BranchId, ItemId, CreatedAtUtc DESC);
-- #endregion

-- #region Purchasing (BRD 8.5)
CREATE TABLE dbo.Suppliers
(
    Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Suppliers PRIMARY KEY,
    TenantId        INT           NOT NULL CONSTRAINT FK_Suppliers_Tenants REFERENCES dbo.Tenants (Id),
    NameEn          NVARCHAR(150) NOT NULL,
    NameAr          NVARCHAR(150) NOT NULL,
    Type            TINYINT       NOT NULL,  -- 1 Distributor, 2 Manufacturer, 3 Other
    TaxNumber       NVARCHAR(30)  NOT NULL,
    Phone           NVARCHAR(30)  NOT NULL,
    PaymentTermDays INT           NOT NULL,
    CreditLimit     DECIMAL(18,2) NOT NULL,
    Balance         DECIMAL(18,2) NOT NULL CONSTRAINT DF_Suppliers_Balance DEFAULT (0),
    IsActive        BIT           NOT NULL CONSTRAINT DF_Suppliers_IsActive DEFAULT (1)
);

CREATE TABLE dbo.PurchaseOrders
(
    Id                   INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PurchaseOrders PRIMARY KEY,
    TenantId             INT           NOT NULL,
    SupplierId           INT           NOT NULL CONSTRAINT FK_PurchaseOrders_Suppliers REFERENCES dbo.Suppliers (Id),
    BranchId             INT           NOT NULL CONSTRAINT FK_PurchaseOrders_Branches REFERENCES dbo.Branches (Id),
    Status               TINYINT       NOT NULL,  -- 1 Draft, 2 PendingApproval, 3 Approved, 4 Sent, 5 PartiallyReceived, 6 Closed, 7 Cancelled
    ExpectedDeliveryDate DATE          NULL,
    Notes                NVARCHAR(500) NOT NULL,
    Total                DECIMAL(18,2) NOT NULL,
    CreatedBy            INT           NOT NULL,
    ApprovedBy           INT           NULL,
    CreatedAtUtc         DATETIME2(0)  NOT NULL CONSTRAINT DF_PurchaseOrders_CreatedAtUtc DEFAULT (SYSUTCDATETIME())
);

CREATE TABLE dbo.PurchaseOrderLines
(
    Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PurchaseOrderLines PRIMARY KEY,
    PurchaseOrderId INT           NOT NULL CONSTRAINT FK_PurchaseOrderLines_Orders REFERENCES dbo.PurchaseOrders (Id),
    TenantId        INT           NOT NULL,
    ItemId          INT           NOT NULL CONSTRAINT FK_PurchaseOrderLines_Items REFERENCES dbo.Items (Id),
    QuantityBoxes   INT           NOT NULL,
    BonusBoxes      INT           NOT NULL,
    UnitCost        DECIMAL(18,2) NOT NULL,
    DiscountPercent DECIMAL(5,2)  NOT NULL,
    ReceivedBoxes   INT           NOT NULL CONSTRAINT DF_PurchaseOrderLines_Received DEFAULT (0),
    CONSTRAINT UQ_PurchaseOrderLines_Order_Item UNIQUE (PurchaseOrderId, ItemId)
);

CREATE TABLE dbo.GoodsReceipts
(
    Id                    INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_GoodsReceipts PRIMARY KEY,
    TenantId              INT           NOT NULL,
    SupplierId            INT           NOT NULL CONSTRAINT FK_GoodsReceipts_Suppliers REFERENCES dbo.Suppliers (Id),
    BranchId              INT           NOT NULL CONSTRAINT FK_GoodsReceipts_Branches REFERENCES dbo.Branches (Id),
    PurchaseOrderId       INT           NULL CONSTRAINT FK_GoodsReceipts_Orders REFERENCES dbo.PurchaseOrders (Id),
    SupplierInvoiceNumber NVARCHAR(50)  NOT NULL,
    ReceivedBy            INT           NOT NULL,
    Total                 DECIMAL(18,2) NOT NULL,
    ReceivedAtUtc         DATETIME2(0)  NOT NULL CONSTRAINT DF_GoodsReceipts_ReceivedAtUtc DEFAULT (SYSUTCDATETIME())
);

CREATE TABLE dbo.GoodsReceiptLines
(
    Id                      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_GoodsReceiptLines PRIMARY KEY,
    GoodsReceiptId          INT           NOT NULL CONSTRAINT FK_GoodsReceiptLines_Receipts REFERENCES dbo.GoodsReceipts (Id),
    TenantId                INT           NOT NULL,
    ItemId                  INT           NOT NULL CONSTRAINT FK_GoodsReceiptLines_Items REFERENCES dbo.Items (Id),
    BatchNumber             NVARCHAR(50)  NOT NULL,
    ExpiryDate              DATE          NOT NULL,
    QuantityBoxes           INT           NOT NULL,
    BonusBoxes              INT           NOT NULL,
    CostPerBox              DECIMAL(18,2) NOT NULL,
    DiscountPercent         DECIMAL(5,2)  NOT NULL,
    VatRate                 DECIMAL(5,4)  NOT NULL,
    ShelfLifeOverrideReason NVARCHAR(300) NULL
);
-- #endregion

-- #region Sales (BRD 8.3)
CREATE TABLE dbo.Shifts
(
    Id           INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Shifts PRIMARY KEY,
    TenantId     INT           NOT NULL,
    BranchId     INT           NOT NULL CONSTRAINT FK_Shifts_Branches REFERENCES dbo.Branches (Id),
    CashierId    INT           NOT NULL CONSTRAINT FK_Shifts_Users REFERENCES dbo.Users (Id),
    TerminalCode NVARCHAR(30)  NOT NULL,
    OpeningFloat DECIMAL(18,2) NOT NULL,
    Status       TINYINT       NOT NULL,  -- 1 Open, 2 Closed
    OpenedAtUtc  DATETIME2(0)  NOT NULL,
    ClosedAtUtc  DATETIME2(0)  NULL,
    ExpectedCash DECIMAL(18,2) NULL,
    CountedCash  DECIMAL(18,2) NULL,
    Variance     DECIMAL(18,2) NULL,
    ClosingNote  NVARCHAR(300) NULL,
    ApprovedBy   INT           NULL
);
-- A cashier has at most one open shift per branch, even if two requests arrive together.
CREATE UNIQUE INDEX UQ_Shifts_OneOpen ON dbo.Shifts (TenantId, BranchId, CashierId) WHERE Status = 1;

CREATE TABLE dbo.CashMovements
(
    Id           INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CashMovements PRIMARY KEY,
    TenantId     INT           NOT NULL,
    ShiftId      INT           NOT NULL CONSTRAINT FK_CashMovements_Shifts REFERENCES dbo.Shifts (Id),
    Type         TINYINT       NOT NULL,  -- 1 PayIn, 2 PayOut
    Amount       DECIMAL(18,2) NOT NULL,
    Reason       NVARCHAR(300) NOT NULL,
    UserId       INT           NOT NULL,
    CreatedAtUtc DATETIME2(0)  NOT NULL CONSTRAINT DF_CashMovements_CreatedAtUtc DEFAULT (SYSUTCDATETIME())
);

CREATE TABLE dbo.Sales
(
    Id                     BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Sales PRIMARY KEY,
    TenantId               INT              NOT NULL,
    BranchId               INT              NOT NULL CONSTRAINT FK_Sales_Branches REFERENCES dbo.Branches (Id),
    ShiftId                INT              NOT NULL CONSTRAINT FK_Sales_Shifts REFERENCES dbo.Shifts (Id),
    CashierId              INT              NOT NULL,
    PharmacistId           INT              NULL,
    OfflineId              UNIQUEIDENTIFIER NULL,
    BuyerName              NVARCHAR(150)    NULL,
    BuyerNationalId        NVARCHAR(20)     NULL,
    BuyerTaxNumber         NVARCHAR(30)     NULL,
    InvoiceDiscountPercent DECIMAL(5,2)     NOT NULL,
    Total                  DECIMAL(18,2)    NOT NULL,
    VatTotal               DECIMAL(18,2)    NOT NULL,
    ChangeGiven            DECIMAL(18,2)    NOT NULL,
    SoldAtUtc              DATETIME2(0)     NOT NULL
);
-- BR-POS-5: the same offline sale can never be stored twice.
CREATE UNIQUE INDEX UQ_Sales_OfflineId ON dbo.Sales (TenantId, OfflineId) WHERE OfflineId IS NOT NULL;
CREATE INDEX IX_Sales_Shift ON dbo.Sales (TenantId, ShiftId);

CREATE TABLE dbo.SaleLines
(
    SaleId           BIGINT        NOT NULL CONSTRAINT FK_SaleLines_Sales REFERENCES dbo.Sales (Id),
    LineNumber       INT           NOT NULL,
    TenantId         INT           NOT NULL,
    ItemId           INT           NOT NULL CONSTRAINT FK_SaleLines_Items REFERENCES dbo.Items (Id),
    ItemName         NVARCHAR(200) NOT NULL,   -- snapshot (BR-POS-4)
    UnitType         TINYINT       NOT NULL,   -- 1 Box, 2 Strip, 3 Unit
    Quantity         INT           NOT NULL,
    BaseUnitsPerUnit INT           NOT NULL,
    UnitPrice        DECIMAL(18,2) NOT NULL,
    DiscountPercent  DECIMAL(5,2)  NOT NULL,
    VatRate          DECIMAL(5,4)  NOT NULL,
    LineTotal        DECIMAL(18,2) NOT NULL,
    VatAmount        DECIMAL(18,2) NOT NULL,
    ReturnedQuantity INT           NOT NULL CONSTRAINT DF_SaleLines_Returned DEFAULT (0),
    CONSTRAINT PK_SaleLines PRIMARY KEY (SaleId, LineNumber),
    CONSTRAINT CK_SaleLines_Returned CHECK (ReturnedQuantity <= Quantity)
);

CREATE TABLE dbo.SaleLineBatches
(
    SaleId           BIGINT NOT NULL,
    LineNumber       INT    NOT NULL,
    BatchId          INT    NOT NULL CONSTRAINT FK_SaleLineBatches_Batches REFERENCES dbo.Batches (Id),
    TenantId         INT    NOT NULL,
    Quantity         INT    NOT NULL,
    ReturnedQuantity INT    NOT NULL CONSTRAINT DF_SaleLineBatches_Returned DEFAULT (0),
    CONSTRAINT PK_SaleLineBatches PRIMARY KEY (SaleId, LineNumber, BatchId),
    CONSTRAINT FK_SaleLineBatches_Lines FOREIGN KEY (SaleId, LineNumber) REFERENCES dbo.SaleLines (SaleId, LineNumber),
    CONSTRAINT CK_SaleLineBatches_Returned CHECK (ReturnedQuantity <= Quantity)
);

CREATE TABLE dbo.SalePayments
(
    Id        BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SalePayments PRIMARY KEY,
    SaleId    BIGINT        NOT NULL CONSTRAINT FK_SalePayments_Sales REFERENCES dbo.Sales (Id),
    TenantId  INT           NOT NULL,
    Method    TINYINT       NOT NULL,  -- 1 Cash, 2 Card, 3 MobileWallet, 4 Fawry, 5 InstaPay
    Amount    DECIMAL(18,2) NOT NULL,
    Reference NVARCHAR(100) NULL
);

CREATE TABLE dbo.SaleReturns
(
    Id            BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SaleReturns PRIMARY KEY,
    TenantId      INT           NOT NULL,
    SaleId        BIGINT        NOT NULL CONSTRAINT FK_SaleReturns_Sales REFERENCES dbo.Sales (Id),
    BranchId      INT           NOT NULL,
    ShiftId       INT           NOT NULL CONSTRAINT FK_SaleReturns_Shifts REFERENCES dbo.Shifts (Id),
    CashierId     INT           NOT NULL,
    RefundMethod  TINYINT       NOT NULL,  -- 1 Cash, 2 OriginalMethod, 3 StoreCredit
    Reason        NVARCHAR(300) NOT NULL,
    RefundTotal   DECIMAL(18,2) NOT NULL,
    ReturnedAtUtc DATETIME2(0)  NOT NULL
);

CREATE TABLE dbo.SaleReturnLines
(
    Id           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SaleReturnLines PRIMARY KEY,
    SaleReturnId BIGINT        NOT NULL CONSTRAINT FK_SaleReturnLines_Returns REFERENCES dbo.SaleReturns (Id),
    TenantId     INT           NOT NULL,
    LineNumber   INT           NOT NULL,
    Quantity     INT           NOT NULL,
    IsResaleable BIT           NOT NULL,
    RefundAmount DECIMAL(18,2) NOT NULL
);
-- #endregion

-- #region Tax (BRD 8.9)
CREATE TABLE dbo.EtaDocuments
(
    Id                 BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EtaDocuments PRIMARY KEY,
    TenantId           INT           NOT NULL,
    BranchId           INT           NOT NULL,
    Type               TINYINT       NOT NULL,  -- 1 Receipt, 2 Invoice, 3 ReturnReceipt, 4 CreditNote
    SaleId             BIGINT        NULL CONSTRAINT FK_EtaDocuments_Sales REFERENCES dbo.Sales (Id),
    SaleReturnId       BIGINT        NULL CONSTRAINT FK_EtaDocuments_Returns REFERENCES dbo.SaleReturns (Id),
    OriginalDocumentId BIGINT        NULL CONSTRAINT FK_EtaDocuments_Original REFERENCES dbo.EtaDocuments (Id),  -- ACC-06
    Status             TINYINT       NOT NULL,  -- 1 Draft, 2 Queued, 3 Submitted, 4 Valid, 5 Invalid, 6 Cancelled
    Uuid               NVARCHAR(100) NULL,
    LongId             NVARCHAR(200) NULL,
    QrCode             NVARCHAR(MAX) NULL,
    Errors             NVARCHAR(MAX) NULL,
    Attempts           INT           NOT NULL CONSTRAINT DF_EtaDocuments_Attempts DEFAULT (0),
    CreatedAtUtc       DATETIME2(0)  NOT NULL,
    SubmittedAtUtc     DATETIME2(0)  NULL
);
CREATE INDEX IX_EtaDocuments_Queue ON dbo.EtaDocuments (Status, CreatedAtUtc);
CREATE INDEX IX_EtaDocuments_Sale ON dbo.EtaDocuments (TenantId, SaleId);
-- #endregion

-- #region Prescriptions (BRD 8.6)
CREATE TABLE dbo.Patients
(
    Id           INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Patients PRIMARY KEY,
    TenantId     INT           NOT NULL CONSTRAINT FK_Patients_Tenants REFERENCES dbo.Tenants (Id),
    FullName     NVARCHAR(150) NOT NULL,
    NationalId   NVARCHAR(14)  NULL,
    Mobile       NVARCHAR(20)  NULL,
    BirthDate    DATE          NULL,
    CreatedAtUtc DATETIME2(0)  NOT NULL CONSTRAINT DF_Patients_CreatedAtUtc DEFAULT (SYSUTCDATETIME())
);
CREATE INDEX IX_Patients_Name ON dbo.Patients (TenantId, FullName);
CREATE UNIQUE INDEX UQ_Patients_NationalId ON dbo.Patients (TenantId, NationalId) WHERE NationalId IS NOT NULL;

CREATE TABLE dbo.PatientAllergies
(
    PatientId    INT           NOT NULL CONSTRAINT FK_PatientAllergies_Patients REFERENCES dbo.Patients (Id),
    TenantId     INT           NOT NULL,
    AllergenCode NVARCHAR(50)  NOT NULL,  -- an ingredient code or a class code
    Note         NVARCHAR(200) NULL,
    CONSTRAINT PK_PatientAllergies PRIMARY KEY (PatientId, AllergenCode)
);

CREATE TABLE dbo.Prescribers
(
    Id            INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Prescribers PRIMARY KEY,
    TenantId      INT           NOT NULL CONSTRAINT FK_Prescribers_Tenants REFERENCES dbo.Tenants (Id),
    FullName      NVARCHAR(150) NOT NULL,
    Specialty     NVARCHAR(100) NULL,
    LicenceNumber NVARCHAR(50)  NULL,
    Clinic        NVARCHAR(150) NULL
);
CREATE INDEX IX_Prescribers_Name ON dbo.Prescribers (TenantId, FullName);

CREATE TABLE dbo.Prescriptions
(
    Id                 INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Prescriptions PRIMARY KEY,
    TenantId           INT           NOT NULL,
    BranchId           INT           NOT NULL CONSTRAINT FK_Prescriptions_Branches REFERENCES dbo.Branches (Id),
    PatientId          INT           NOT NULL CONSTRAINT FK_Prescriptions_Patients REFERENCES dbo.Patients (Id),
    PrescriberId       INT           NOT NULL CONSTRAINT FK_Prescriptions_Prescribers REFERENCES dbo.Prescribers (Id),
    PrescriptionNumber NVARCHAR(50)  NULL,
    PrescribedOn       DATE          NOT NULL,
    Diagnosis          NVARCHAR(300) NULL,
    Status             TINYINT       NOT NULL,  -- 1 Open, 2 PartiallyDispensed, 3 Completed, 4 Cancelled
    RecordedBy         INT           NOT NULL CONSTRAINT FK_Prescriptions_Users REFERENCES dbo.Users (Id),
    RecordedAtUtc      DATETIME2(0)  NOT NULL
);
CREATE INDEX IX_Prescriptions_Patient ON dbo.Prescriptions (TenantId, PatientId);
CREATE INDEX IX_Prescriptions_Open ON dbo.Prescriptions (TenantId, Status) WHERE Status IN (1, 2);

CREATE TABLE dbo.PrescriptionLines
(
    PrescriptionId     INT           NOT NULL CONSTRAINT FK_PrescriptionLines_Prescriptions REFERENCES dbo.Prescriptions (Id),
    LineNumber         INT           NOT NULL,
    TenantId           INT           NOT NULL,
    ItemId             INT           NOT NULL CONSTRAINT FK_PrescriptionLines_Items REFERENCES dbo.Items (Id),
    ItemName           NVARCHAR(200) NOT NULL,
    Unit               TINYINT       NOT NULL,
    Quantity           INT           NOT NULL CONSTRAINT CK_PrescriptionLines_Quantity CHECK (Quantity > 0),
    RefillsAllowed     INT           NOT NULL CONSTRAINT CK_PrescriptionLines_Refills CHECK (RefillsAllowed BETWEEN 0 AND 12),
    BaseUnitsPerUnit   INT           NOT NULL,
    DoseUnits          DECIMAL(9,3)  NULL,
    FrequencyPerDay    INT           NULL,
    DurationDays       INT           NULL,
    Instructions       NVARCHAR(300) NULL,
    DispensedBaseUnits INT           NOT NULL CONSTRAINT DF_PrescriptionLines_Dispensed DEFAULT (0),
    CONSTRAINT PK_PrescriptionLines PRIMARY KEY (PrescriptionId, LineNumber),
    -- RX-06: the final guard against dispensing more than the first fill plus the refills.
    CONSTRAINT CK_PrescriptionLines_Dispensed
        CHECK (DispensedBaseUnits >= 0 AND DispensedBaseUnits <= Quantity * (1 + RefillsAllowed) * BaseUnitsPerUnit)
);

-- RX-05 / BR-RX-3: the image itself is stored encrypted outside the database; this is its index.
CREATE TABLE dbo.PrescriptionImages
(
    Id             INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrescriptionImages PRIMARY KEY,
    TenantId       INT           NOT NULL,
    PrescriptionId INT           NOT NULL CONSTRAINT FK_PrescriptionImages_Prescriptions REFERENCES dbo.Prescriptions (Id),
    StorageKey     NVARCHAR(200) NOT NULL,
    ContentType    NVARCHAR(50)  NOT NULL,
    SizeBytes      INT           NOT NULL,
    UploadedBy     INT           NOT NULL CONSTRAINT FK_PrescriptionImages_Users REFERENCES dbo.Users (Id),
    UploadedAtUtc  DATETIME2(0)  NOT NULL
);

CREATE TABLE dbo.Dispenses
(
    Id                  BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Dispenses PRIMARY KEY,
    TenantId            INT            NOT NULL,
    BranchId            INT            NOT NULL CONSTRAINT FK_Dispenses_Branches REFERENCES dbo.Branches (Id),
    PrescriptionId      INT            NULL CONSTRAINT FK_Dispenses_Prescriptions REFERENCES dbo.Prescriptions (Id),
    PatientId           INT            NULL CONSTRAINT FK_Dispenses_Patients REFERENCES dbo.Patients (Id),
    SaleId              BIGINT         NOT NULL CONSTRAINT FK_Dispenses_Sales REFERENCES dbo.Sales (Id),
    PharmacistId        INT            NOT NULL CONSTRAINT FK_Dispenses_Users REFERENCES dbo.Users (Id),
    CounsellingNote     NVARCHAR(1000) NULL,  -- RX-10
    PrescriptionPending BIT            NOT NULL, -- BR-RX-1 "record later"
    DispensedAtUtc      DATETIME2(0)   NOT NULL
);
CREATE INDEX IX_Dispenses_Patient ON dbo.Dispenses (TenantId, PatientId, DispensedAtUtc DESC);
CREATE INDEX IX_Dispenses_Sale ON dbo.Dispenses (TenantId, SaleId);

CREATE TABLE dbo.DispenseLines
(
    DispenseId             BIGINT NOT NULL CONSTRAINT FK_DispenseLines_Dispenses REFERENCES dbo.Dispenses (Id),
    SaleLineNumber         INT    NOT NULL,
    TenantId               INT    NOT NULL,
    PrescriptionLineNumber INT    NULL,
    ItemId                 INT    NOT NULL,
    BaseUnits              INT    NOT NULL,
    CONSTRAINT PK_DispenseLines PRIMARY KEY (DispenseId, SaleLineNumber)
);

-- Key data "Dispense": the alerts shown and the overrides with their reasons (RX-03, BR-RX-2).
CREATE TABLE dbo.DispenseAlerts
(
    Id             BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DispenseAlerts PRIMARY KEY,
    DispenseId     BIGINT        NOT NULL CONSTRAINT FK_DispenseAlerts_Dispenses REFERENCES dbo.Dispenses (Id),
    TenantId       INT           NOT NULL,
    Code           NVARCHAR(150) NOT NULL,
    Type           TINYINT       NOT NULL,
    Severity       TINYINT       NOT NULL,
    Message        NVARCHAR(600) NOT NULL,
    OverrideReason NVARCHAR(500) NULL
);
-- #endregion

-- #region Controlled drugs register (BRD 8.7)
CREATE TABLE dbo.ControlledRegister
(
    Id                  BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ControlledRegister PRIMARY KEY,
    TenantId            INT           NOT NULL,
    BranchId            INT           NOT NULL CONSTRAINT FK_ControlledRegister_Branches REFERENCES dbo.Branches (Id),
    ItemId              INT           NOT NULL CONSTRAINT FK_ControlledRegister_Items REFERENCES dbo.Items (Id),
    BatchId             INT           NULL CONSTRAINT FK_ControlledRegister_Batches REFERENCES dbo.Batches (Id),
    Type                TINYINT       NOT NULL,  -- 1 Purchase, 2 Dispense, 3 Destruction, 4 Adjustment, 5 Correction, 6 TransferOut, 7 TransferIn
    Quantity            INT           NOT NULL,  -- signed, smallest unit
    Balance             INT           NOT NULL CONSTRAINT CK_ControlledRegister_Balance CHECK (Balance >= 0),
    OccurredAtUtc       DATETIME2(0)  NOT NULL,
    DocumentType        NVARCHAR(30)  NOT NULL,
    DocumentId          BIGINT        NOT NULL,
    PatientId           INT           NULL,
    PatientName         NVARCHAR(150) NULL,
    PatientNationalId   NVARCHAR(14)  NULL,
    PrescriberName      NVARCHAR(150) NULL,
    PrescriberLicence   NVARCHAR(50)  NULL,
    PrescriptionId      INT           NULL,
    PrescriptionNumber  NVARCHAR(50)  NULL,
    PrescriptionDate    DATE          NULL,
    PrescriptionImageId INT           NULL,
    PharmacistId        INT           NOT NULL CONSTRAINT FK_ControlledRegister_Users REFERENCES dbo.Users (Id),
    CorrectsEntryId     BIGINT        NULL CONSTRAINT FK_ControlledRegister_Corrects REFERENCES dbo.ControlledRegister (Id),
    Note                NVARCHAR(500) NULL,
    RecordedAtUtc       DATETIME2(0)  NOT NULL CONSTRAINT DF_ControlledRegister_RecordedAtUtc DEFAULT (SYSUTCDATETIME())
);
CREATE INDEX IX_ControlledRegister_Item ON dbo.ControlledRegister (TenantId, BranchId, ItemId, Id);
CREATE INDEX IX_ControlledRegister_Patient ON dbo.ControlledRegister (TenantId, PatientId, ItemId, OccurredAtUtc)
    WHERE PatientId IS NOT NULL;
GO

-- CTL-05 / BR-CTL-2: register entries cannot be changed or deleted by any role, Owner included.
CREATE TRIGGER dbo.TR_ControlledRegister_Immutable ON dbo.ControlledRegister
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51001, 'Controlled register entries are append-only: add a correction entry instead.', 1;
END
GO

-- CTL-06: signed weekly counts of the locked cabinet.
CREATE TABLE dbo.ControlledBalanceChecks
(
    Id              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ControlledBalanceChecks PRIMARY KEY,
    TenantId        INT           NOT NULL,
    BranchId        INT           NOT NULL CONSTRAINT FK_ControlledBalanceChecks_Branches REFERENCES dbo.Branches (Id),
    ItemId          INT           NOT NULL CONSTRAINT FK_ControlledBalanceChecks_Items REFERENCES dbo.Items (Id),
    CountedQuantity INT           NOT NULL,
    RegisterBalance INT           NOT NULL,
    StockQuantity   INT           NOT NULL,
    IsMismatch      BIT           NOT NULL,
    PharmacistId    INT           NOT NULL CONSTRAINT FK_ControlledBalanceChecks_Users REFERENCES dbo.Users (Id),
    Note            NVARCHAR(500) NULL,
    CheckedAtUtc    DATETIME2(0)  NOT NULL
);
CREATE INDEX IX_ControlledBalanceChecks_Item ON dbo.ControlledBalanceChecks (TenantId, BranchId, ItemId, CheckedAtUtc DESC);
GO

-- A signed count is evidence for the inspector: it cannot be changed either.
CREATE TRIGGER dbo.TR_ControlledBalanceChecks_Immutable ON dbo.ControlledBalanceChecks
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51002, 'Balance checks are signed records and cannot be changed or deleted.', 1;
END
GO
-- #endregion

-- #region Multi-branch and warehouse (BRD 8.8)
-- MBR-02: per-branch price, min/max and visibility; a missing row means "use the central item".
CREATE TABLE dbo.BranchItemSettings
(
    Id           INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_BranchItemSettings PRIMARY KEY,
    TenantId     INT           NOT NULL,
    BranchId     INT           NOT NULL CONSTRAINT FK_BranchItemSettings_Branches REFERENCES dbo.Branches (Id),
    ItemId       INT           NOT NULL CONSTRAINT FK_BranchItemSettings_Items REFERENCES dbo.Items (Id),
    SellingPrice DECIMAL(18,2) NULL CONSTRAINT CK_BranchItemSettings_Price CHECK (SellingPrice > 0),
    MinStock     INT           NULL,
    MaxStock     INT           NULL,
    IsHidden     BIT           NOT NULL CONSTRAINT DF_BranchItemSettings_IsHidden DEFAULT (0),
    CONSTRAINT UQ_BranchItemSettings UNIQUE (TenantId, BranchId, ItemId),
    CONSTRAINT CK_BranchItemSettings_MinMax CHECK (MinStock IS NULL OR MaxStock IS NULL OR MinStock <= MaxStock)
);

CREATE TABLE dbo.Transfers
(
    Id             INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Transfers PRIMARY KEY,
    TenantId       INT           NOT NULL,
    FromBranchId   INT           NOT NULL CONSTRAINT FK_Transfers_From REFERENCES dbo.Branches (Id),
    ToBranchId     INT           NOT NULL CONSTRAINT FK_Transfers_To REFERENCES dbo.Branches (Id),
    Status         TINYINT       NOT NULL,  -- 1 Requested, 2 Approved, 3 InTransit, 4 Received, 5 ReceivedWithShortage, 6 Rejected, 7 Cancelled
    IsDirect       BIT           NOT NULL,  -- MBR-04
    Note           NVARCHAR(300) NULL,
    RequestedBy    INT           NOT NULL CONSTRAINT FK_Transfers_RequestedBy REFERENCES dbo.Users (Id),
    RequestedAtUtc DATETIME2(0)  NOT NULL,
    ApprovedBy     INT           NULL,
    ApprovedAtUtc  DATETIME2(0)  NULL,
    ShippedBy      INT           NULL,
    ShippedAtUtc   DATETIME2(0)  NULL,
    ReceivedBy     INT           NULL,
    ReceivedAtUtc  DATETIME2(0)  NULL,
    ClosingNote    NVARCHAR(300) NULL,
    CONSTRAINT CK_Transfers_Locations CHECK (FromBranchId <> ToBranchId)
);
CREATE INDEX IX_Transfers_Open ON dbo.Transfers (TenantId, Status) INCLUDE (FromBranchId, ToBranchId);

CREATE TABLE dbo.TransferLines
(
    TransferId        INT           NOT NULL CONSTRAINT FK_TransferLines_Transfers REFERENCES dbo.Transfers (Id),
    LineNumber        INT           NOT NULL,
    TenantId          INT           NOT NULL,
    ItemId            INT           NOT NULL CONSTRAINT FK_TransferLines_Items REFERENCES dbo.Items (Id),
    ItemName          NVARCHAR(200) NOT NULL,
    RequestedQuantity INT           NOT NULL CONSTRAINT CK_TransferLines_Quantity CHECK (RequestedQuantity > 0),  -- smallest unit
    CONSTRAINT PK_TransferLines PRIMARY KEY (TransferId, LineNumber)
);

-- BR-MBR-2: batch number, expiry and cost travel with the stock. While QuantityReceived is NULL the
-- units are in transit (BR-MBR-1); afterwards QuantitySent - QuantityReceived is the shortage (AC-MBR-1).
CREATE TABLE dbo.TransferBatches
(
    TransferId         INT           NOT NULL CONSTRAINT FK_TransferBatches_Transfers REFERENCES dbo.Transfers (Id),
    SourceBatchId      INT           NOT NULL CONSTRAINT FK_TransferBatches_Source REFERENCES dbo.Batches (Id),
    TenantId           INT           NOT NULL,
    LineNumber         INT           NOT NULL,
    BatchNumber        NVARCHAR(50)  NOT NULL,
    ExpiryDate         DATE          NOT NULL,
    UnitCost           DECIMAL(18,6) NOT NULL,
    QuantitySent       INT           NOT NULL CONSTRAINT CK_TransferBatches_Sent CHECK (QuantitySent > 0),
    QuantityReceived   INT           NULL,
    DestinationBatchId INT           NULL CONSTRAINT FK_TransferBatches_Destination REFERENCES dbo.Batches (Id),
    CONSTRAINT PK_TransferBatches PRIMARY KEY (TransferId, SourceBatchId),
    CONSTRAINT CK_TransferBatches_Received CHECK (QuantityReceived IS NULL OR QuantityReceived BETWEEN 0 AND QuantitySent)
);
-- #endregion
