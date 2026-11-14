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
    IsControlled   BIT           NOT NULL,
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
    Type         TINYINT       NOT NULL,  -- 1 Purchase, 2 Sale, 3 SaleReturn, 4 Adjustment
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
