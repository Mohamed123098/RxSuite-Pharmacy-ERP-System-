/* Purchasing procedures (BRD 8.5). Child lines arrive as a JSON array and are read with OPENJSON,
   so a whole document is saved in one round trip. */

-- #region usp_Supplier_Insert : creates a supplier and returns its Id
CREATE OR ALTER PROCEDURE dbo.usp_Supplier_Insert
    @TenantId INT, @NameEn NVARCHAR(150), @NameAr NVARCHAR(150), @Type TINYINT, @TaxNumber NVARCHAR(30),
    @Phone NVARCHAR(30), @PaymentTermDays INT, @CreditLimit DECIMAL(18,2)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.Suppliers (TenantId, NameEn, NameAr, Type, TaxNumber, Phone, PaymentTermDays, CreditLimit)
    VALUES (@TenantId, @NameEn, @NameAr, @Type, @TaxNumber, @Phone, @PaymentTermDays, @CreditLimit);

    SELECT CAST(SCOPE_IDENTITY() AS INT);
END
GO
-- #endregion

-- #region usp_Supplier_GetById : one supplier of the tenant, or no row
CREATE OR ALTER PROCEDURE dbo.usp_Supplier_GetById
    @TenantId INT, @SupplierId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, TenantId, NameEn, NameAr, Type, TaxNumber, Phone, PaymentTermDays, CreditLimit, Balance, IsActive
    FROM dbo.Suppliers
    WHERE Id = @SupplierId AND TenantId = @TenantId;
END
GO
-- #endregion

-- #region usp_Supplier_GetAll : the suppliers list with balances (PUR-10)
CREATE OR ALTER PROCEDURE dbo.usp_Supplier_GetAll
    @TenantId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, NameEn, NameAr, Type, Phone, PaymentTermDays, CreditLimit, Balance
    FROM dbo.Suppliers
    WHERE TenantId = @TenantId AND IsActive = 1
    ORDER BY NameEn;
END
GO
-- #endregion

-- #region usp_Supplier_IncreaseBalance : adds a received invoice to what the tenant owes the supplier
CREATE OR ALTER PROCEDURE dbo.usp_Supplier_IncreaseBalance
    @TenantId INT, @SupplierId INT, @Amount DECIMAL(18,2)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.Suppliers SET Balance = Balance + @Amount WHERE Id = @SupplierId AND TenantId = @TenantId;
END
GO
-- #endregion

-- #region usp_PurchaseOrder_Insert : saves an order with its lines and returns its Id
/* @LinesJson: [{ "ItemId", "QuantityBoxes", "BonusBoxes", "UnitCost", "DiscountPercent" }] */
CREATE OR ALTER PROCEDURE dbo.usp_PurchaseOrder_Insert
    @TenantId INT, @SupplierId INT, @BranchId INT, @Status TINYINT, @ExpectedDeliveryDate DATE = NULL,
    @Notes NVARCHAR(500), @Total DECIMAL(18,2), @CreatedBy INT, @LinesJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.PurchaseOrders (TenantId, SupplierId, BranchId, Status, ExpectedDeliveryDate, Notes, Total, CreatedBy)
    VALUES (@TenantId, @SupplierId, @BranchId, @Status, @ExpectedDeliveryDate, @Notes, @Total, @CreatedBy);

    DECLARE @PurchaseOrderId INT = CAST(SCOPE_IDENTITY() AS INT);

    INSERT INTO dbo.PurchaseOrderLines (PurchaseOrderId, TenantId, ItemId, QuantityBoxes, BonusBoxes, UnitCost, DiscountPercent)
    SELECT @PurchaseOrderId, @TenantId, j.ItemId, j.QuantityBoxes, j.BonusBoxes, j.UnitCost, j.DiscountPercent
    FROM OPENJSON(@LinesJson)
         WITH (ItemId INT, QuantityBoxes INT, BonusBoxes INT, UnitCost DECIMAL(18,2), DiscountPercent DECIMAL(5,2)) j;

    SELECT @PurchaseOrderId;
END
GO
-- #endregion

-- #region usp_PurchaseOrder_GetById : result set 1 = the order, result set 2 = its lines
CREATE OR ALTER PROCEDURE dbo.usp_PurchaseOrder_GetById
    @TenantId INT, @PurchaseOrderId INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Id, TenantId, SupplierId, BranchId, Status, ExpectedDeliveryDate, Notes, Total, CreatedBy, ApprovedBy
    FROM dbo.PurchaseOrders
    WHERE Id = @PurchaseOrderId AND TenantId = @TenantId;

    SELECT ItemId, QuantityBoxes, BonusBoxes, UnitCost, DiscountPercent, ReceivedBoxes
    FROM dbo.PurchaseOrderLines
    WHERE PurchaseOrderId = @PurchaseOrderId AND TenantId = @TenantId
    ORDER BY Id;
END
GO
-- #endregion

-- #region usp_PurchaseOrder_Update : saves status, approver and received quantities
/* @LinesJson: [{ "ItemId", "ReceivedBoxes" }] */
CREATE OR ALTER PROCEDURE dbo.usp_PurchaseOrder_Update
    @TenantId INT, @PurchaseOrderId INT, @Status TINYINT, @ApprovedBy INT = NULL, @LinesJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE dbo.PurchaseOrders
    SET Status = @Status, ApprovedBy = @ApprovedBy
    WHERE Id = @PurchaseOrderId AND TenantId = @TenantId;

    UPDATE l
    SET l.ReceivedBoxes = j.ReceivedBoxes
    FROM dbo.PurchaseOrderLines l
    JOIN OPENJSON(@LinesJson) WITH (ItemId INT, ReceivedBoxes INT) j ON j.ItemId = l.ItemId
    WHERE l.PurchaseOrderId = @PurchaseOrderId AND l.TenantId = @TenantId;
END
GO
-- #endregion

-- #region usp_GoodsReceipt_Insert : saves a goods receipt with its lines and returns its Id
/* @LinesJson: [{ "ItemId", "BatchNumber", "ExpiryDate", "QuantityBoxes", "BonusBoxes", "CostPerBox",
                  "DiscountPercent", "VatRate", "ShelfLifeOverrideReason" }]
   Stock is added separately, per line, by usp_Batch_Receive in the same transaction. */
CREATE OR ALTER PROCEDURE dbo.usp_GoodsReceipt_Insert
    @TenantId INT, @SupplierId INT, @BranchId INT, @PurchaseOrderId INT = NULL,
    @SupplierInvoiceNumber NVARCHAR(50), @ReceivedBy INT, @Total DECIMAL(18,2), @LinesJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.GoodsReceipts (TenantId, SupplierId, BranchId, PurchaseOrderId, SupplierInvoiceNumber, ReceivedBy, Total)
    VALUES (@TenantId, @SupplierId, @BranchId, @PurchaseOrderId, @SupplierInvoiceNumber, @ReceivedBy, @Total);

    DECLARE @GoodsReceiptId INT = CAST(SCOPE_IDENTITY() AS INT);

    INSERT INTO dbo.GoodsReceiptLines (GoodsReceiptId, TenantId, ItemId, BatchNumber, ExpiryDate, QuantityBoxes, BonusBoxes,
                                       CostPerBox, DiscountPercent, VatRate, ShelfLifeOverrideReason)
    SELECT @GoodsReceiptId, @TenantId, j.ItemId, j.BatchNumber, j.ExpiryDate, j.QuantityBoxes, j.BonusBoxes,
           j.CostPerBox, j.DiscountPercent, j.VatRate, j.ShelfLifeOverrideReason
    FROM OPENJSON(@LinesJson)
         WITH (ItemId INT, BatchNumber NVARCHAR(50), ExpiryDate DATE, QuantityBoxes INT, BonusBoxes INT,
               CostPerBox DECIMAL(18,2), DiscountPercent DECIMAL(5,2), VatRate DECIMAL(5,4),
               ShelfLifeOverrideReason NVARCHAR(300)) j;

    SELECT @GoodsReceiptId;
END
GO
-- #endregion
