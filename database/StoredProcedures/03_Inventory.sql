/* Item master and stock procedures (BRD 8.4, POS search of 8.3). Quantities are in the smallest unit. */

-- #region usp_Item_Insert : creates an item with its barcodes (JSON array of strings) and returns its Id
CREATE OR ALTER PROCEDURE dbo.usp_Item_Insert
    @TenantId INT, @Code NVARCHAR(30), @NameEn NVARCHAR(200), @NameAr NVARCHAR(200), @ScientificName NVARCHAR(200),
    @Strength NVARCHAR(50), @DosageForm NVARCHAR(50), @StripsPerBox INT, @UnitsPerStrip INT,
    @OfficialPrice DECIMAL(18,2) = NULL, @SellingPrice DECIMAL(18,2), @VatRate DECIMAL(5,4),
    @EtaItemCode NVARCHAR(50) = NULL, @IsRxOnly BIT, @ControlSchedule TINYINT, @DrugId INT = NULL,
    @ControlledLimitPerPrescription INT = NULL, @ControlledLimitPerPatient INT = NULL, @ReorderLevel INT,
    @BarcodesJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.Items (TenantId, Code, NameEn, NameAr, ScientificName, Strength, DosageForm, StripsPerBox, UnitsPerStrip,
                           OfficialPrice, SellingPrice, VatRate, EtaItemCode, IsRxOnly, ControlSchedule, DrugId,
                           ControlledLimitPerPrescription, ControlledLimitPerPatient, ReorderLevel)
    VALUES (@TenantId, @Code, @NameEn, @NameAr, @ScientificName, @Strength, @DosageForm, @StripsPerBox, @UnitsPerStrip,
            @OfficialPrice, @SellingPrice, @VatRate, @EtaItemCode, @IsRxOnly, @ControlSchedule, @DrugId,
            @ControlledLimitPerPrescription, @ControlledLimitPerPatient, @ReorderLevel);

    DECLARE @ItemId INT = CAST(SCOPE_IDENTITY() AS INT);

    INSERT INTO dbo.ItemBarcodes (TenantId, ItemId, Barcode)
    SELECT @TenantId, @ItemId, j.[value] FROM OPENJSON(@BarcodesJson) j;

    SELECT @ItemId;
END
GO
-- #endregion

-- #region usp_Item_CodeExists : 1 when the tenant already uses this item code
CREATE OR ALTER PROCEDURE dbo.usp_Item_CodeExists
    @TenantId INT, @Code NVARCHAR(30)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.Items WHERE TenantId = @TenantId AND Code = @Code)
                     THEN 1 ELSE 0 END AS BIT);
END
GO
-- #endregion

-- #region usp_Item_GetById : one item of the tenant, or no row
CREATE OR ALTER PROCEDURE dbo.usp_Item_GetById
    @TenantId INT, @ItemId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, TenantId, Code, NameEn, NameAr, ScientificName, Strength, DosageForm, StripsPerBox, UnitsPerStrip,
           OfficialPrice, SellingPrice, VatRate, EtaItemCode, IsRxOnly, ControlSchedule, DrugId,
           ControlledLimitPerPrescription, ControlledLimitPerPatient, ReorderLevel, IsActive
    FROM dbo.Items
    WHERE Id = @ItemId AND TenantId = @TenantId;
END
GO
-- #endregion

-- #region usp_Item_GetByIds : several items of the tenant (JSON array of Ids), in one round trip
CREATE OR ALTER PROCEDURE dbo.usp_Item_GetByIds
    @TenantId INT, @ItemIdsJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, TenantId, Code, NameEn, NameAr, ScientificName, Strength, DosageForm, StripsPerBox, UnitsPerStrip,
           OfficialPrice, SellingPrice, VatRate, EtaItemCode, IsRxOnly, ControlSchedule, DrugId,
           ControlledLimitPerPrescription, ControlledLimitPerPatient, ReorderLevel, IsActive
    FROM dbo.Items
    WHERE TenantId = @TenantId
      AND Id IN (SELECT CAST(j.[value] AS INT) FROM OPENJSON(@ItemIdsJson) j);
END
GO
-- #endregion

-- #region usp_Item_UpdatePrice : changes the selling price of a box
CREATE OR ALTER PROCEDURE dbo.usp_Item_UpdatePrice
    @TenantId INT, @ItemId INT, @SellingPrice DECIMAL(18,2)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.Items SET SellingPrice = @SellingPrice WHERE Id = @ItemId AND TenantId = @TenantId;
END
GO
-- #endregion

-- #region usp_Item_Search : POS search (POS-01/02) by barcode, code or name, with sellable stock and nearest expiry
CREATE OR ALTER PROCEDURE dbo.usp_Item_Search
    @TenantId INT, @BranchId INT, @Term NVARCHAR(100), @Today DATE
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (30)
           i.Id, i.Code, i.NameEn, i.NameAr, i.ScientificName, i.Strength, i.DosageForm, i.SellingPrice,
           i.StripsPerBox * i.UnitsPerStrip AS UnitsPerBox,
           ISNULL(s.AvailableQuantity, 0) AS AvailableQuantity, s.NearestExpiry, i.IsRxOnly,
           CAST(CASE WHEN i.ControlSchedule > 0 THEN 1 ELSE 0 END AS BIT) AS IsControlled
    FROM dbo.Items i
    OUTER APPLY (SELECT SUM(b.Quantity) AS AvailableQuantity, MIN(b.ExpiryDate) AS NearestExpiry
                 FROM dbo.Batches b
                 WHERE b.TenantId = i.TenantId AND b.ItemId = i.Id AND b.BranchId = @BranchId
                   AND b.Status = 1 AND b.Quantity > 0 AND b.ExpiryDate >= @Today) s   -- sellable stock only (POS-05)
    WHERE i.TenantId = @TenantId
      AND i.IsActive = 1
      AND (i.Code LIKE @Term + N'%'
           OR i.NameEn LIKE N'%' + @Term + N'%'
           OR i.NameAr LIKE N'%' + @Term + N'%'
           OR i.ScientificName LIKE N'%' + @Term + N'%'
           OR EXISTS (SELECT 1 FROM dbo.ItemBarcodes bc
                      WHERE bc.TenantId = i.TenantId AND bc.ItemId = i.Id AND bc.Barcode = @Term))
    ORDER BY i.NameEn, i.Id;
END
GO
-- #endregion

-- #region usp_Batch_GetByItems : every batch of the given items in a branch (the application picks by FEFO)
CREATE OR ALTER PROCEDURE dbo.usp_Batch_GetByItems
    @TenantId INT, @BranchId INT, @ItemIdsJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, TenantId, BranchId, ItemId, BatchNumber, ExpiryDate, Quantity, UnitCost, Status
    FROM dbo.Batches
    WHERE TenantId = @TenantId AND BranchId = @BranchId AND Quantity > 0
      AND ItemId IN (SELECT CAST(j.[value] AS INT) FROM OPENJSON(@ItemIdsJson) j);
END
GO
-- #endregion

-- #region usp_Batch_GetById : one batch of the tenant, or no row
CREATE OR ALTER PROCEDURE dbo.usp_Batch_GetById
    @TenantId INT, @BatchId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, TenantId, BranchId, ItemId, BatchNumber, ExpiryDate, Quantity, UnitCost, Status
    FROM dbo.Batches
    WHERE Id = @BatchId AND TenantId = @TenantId;
END
GO
-- #endregion

-- #region usp_Stock_ApplyMovements : applies signed movements to batches and writes the stock card
/* @MovementsJson: [{ "BatchId", "Quantity" (signed), "Type", "DocumentType", "DocumentId", "Reason" }]
   Returns 1 when applied. Returns 0 and changes NOTHING when a batch is missing or would go below
   zero - also when two cashiers sell the last box at the same moment: the UPDATE checks the
   quantity in the same statement that changes it, so only one of them can win. */
CREATE OR ALTER PROCEDURE dbo.usp_Stock_ApplyMovements
    @TenantId INT, @UserId INT, @BranchId INT, @MovementsJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Movements TABLE (BatchId INT NOT NULL, Quantity INT NOT NULL, MovementType TINYINT NOT NULL,
                              DocumentType NVARCHAR(30) NOT NULL, DocumentId BIGINT NOT NULL, Reason NVARCHAR(300) NULL);

    INSERT INTO @Movements (BatchId, Quantity, MovementType, DocumentType, DocumentId, Reason)
    SELECT j.BatchId, j.Quantity, j.MovementType, j.DocumentType, j.DocumentId, j.Reason
    FROM OPENJSON(@MovementsJson)
         WITH (BatchId INT '$.BatchId', Quantity INT '$.Quantity', MovementType TINYINT '$.Type',
               DocumentType NVARCHAR(30) '$.DocumentType', DocumentId BIGINT '$.DocumentId',
               Reason NVARCHAR(300) '$.Reason') j;

    DECLARE @Expected INT = (SELECT COUNT(DISTINCT BatchId) FROM @Movements);

    -- Work inside the caller's transaction when there is one (savepoint), otherwise open our own.
    DECLARE @OwnTransaction BIT = 0;
    IF @@TRANCOUNT = 0
    BEGIN
        BEGIN TRANSACTION;
        SET @OwnTransaction = 1;
    END
    ELSE
        SAVE TRANSACTION ApplyStockMovements;

    UPDATE b
    SET b.Quantity = b.Quantity + m.Quantity
    FROM dbo.Batches b
    JOIN (SELECT BatchId, SUM(Quantity) AS Quantity FROM @Movements GROUP BY BatchId) m ON m.BatchId = b.Id
    WHERE b.TenantId = @TenantId AND b.BranchId = @BranchId
      AND b.Quantity + m.Quantity >= 0;

    IF @@ROWCOUNT <> @Expected
    BEGIN
        IF @OwnTransaction = 1 ROLLBACK TRANSACTION;
        ELSE ROLLBACK TRANSACTION ApplyStockMovements;

        SELECT CAST(0 AS BIT);
        RETURN;
    END

    INSERT INTO dbo.StockMovements (TenantId, BranchId, BatchId, ItemId, Quantity, Type, DocumentType, DocumentId, Reason, UserId)
    SELECT @TenantId, @BranchId, m.BatchId, b.ItemId, m.Quantity, m.MovementType, m.DocumentType, m.DocumentId, m.Reason, @UserId
    FROM @Movements m
    JOIN dbo.Batches b ON b.Id = m.BatchId;

    IF @OwnTransaction = 1 COMMIT TRANSACTION;

    SELECT CAST(1 AS BIT);
END
GO
-- #endregion

-- #region usp_Batch_Receive : adds received stock to its batch (created when new) and returns the batch Id
/* BR-INV-2: the batch is found by item + batch number + expiry in the branch.
   BR-PUR-2: when the batch already holds stock, its unit cost becomes the weighted average. */
CREATE OR ALTER PROCEDURE dbo.usp_Batch_Receive
    @TenantId INT, @UserId INT, @BranchId INT, @ItemId INT, @BatchNumber NVARCHAR(50), @ExpiryDate DATE,
    @Quantity INT, @UnitCost DECIMAL(18,6), @GoodsReceiptId INT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @BatchId INT;

    -- All right-hand sides read the values from BEFORE the update, so the average is correct.
    UPDATE dbo.Batches WITH (UPDLOCK, HOLDLOCK)
    SET UnitCost = (Quantity * UnitCost + @Quantity * @UnitCost) / NULLIF(Quantity + @Quantity, 0),
        Quantity = Quantity + @Quantity,
        @BatchId = Id
    WHERE TenantId = @TenantId AND BranchId = @BranchId AND ItemId = @ItemId
      AND BatchNumber = @BatchNumber AND ExpiryDate = @ExpiryDate;

    IF @BatchId IS NULL
    BEGIN
        INSERT INTO dbo.Batches (TenantId, BranchId, ItemId, BatchNumber, ExpiryDate, Quantity, UnitCost)
        VALUES (@TenantId, @BranchId, @ItemId, @BatchNumber, @ExpiryDate, @Quantity, @UnitCost);

        SET @BatchId = CAST(SCOPE_IDENTITY() AS INT);
    END

    INSERT INTO dbo.StockMovements (TenantId, BranchId, BatchId, ItemId, Quantity, Type, DocumentType, DocumentId, UserId)
    VALUES (@TenantId, @BranchId, @BatchId, @ItemId, @Quantity, 1, N'GoodsReceipt', @GoodsReceiptId, @UserId);

    SELECT @BatchId;
END
GO
-- #endregion

-- #region usp_Batch_Recall : blocks a recalled batch in every branch of the tenant; returns how many records
CREATE OR ALTER PROCEDURE dbo.usp_Batch_Recall
    @TenantId INT, @ItemId INT, @BatchNumber NVARCHAR(50)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.Batches
    SET Status = 3
    WHERE TenantId = @TenantId AND ItemId = @ItemId AND BatchNumber = @BatchNumber;

    SELECT @@ROWCOUNT;
END
GO
-- #endregion

-- #region usp_Job_Batch_QuarantineExpired : nightly job (INV-08) - expired batches of ALL tenants go to quarantine
CREATE OR ALTER PROCEDURE dbo.usp_Job_Batch_QuarantineExpired
    @Today DATE
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.Batches
    SET Status = 2
    WHERE Status = 1 AND ExpiryDate < @Today;

    SELECT @@ROWCOUNT;
END
GO
-- #endregion

-- #region usp_Stock_GetCard : stock card of an item in a branch (INV-12), newest first
CREATE OR ALTER PROCEDURE dbo.usp_Stock_GetCard
    @TenantId INT, @BranchId INT, @ItemId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (500)
           m.CreatedAtUtc, m.Type, b.BatchNumber, m.Quantity, m.DocumentType, m.DocumentId, m.Reason, u.FullName AS UserName
    FROM dbo.StockMovements m
    JOIN dbo.Batches b ON b.Id = m.BatchId
    LEFT JOIN dbo.Users u ON u.Id = m.UserId
    WHERE m.TenantId = @TenantId AND m.BranchId = @BranchId AND m.ItemId = @ItemId
    ORDER BY m.CreatedAtUtc DESC, m.Id DESC;
END
GO
-- #endregion

-- #region usp_Stock_GetNearExpiry : batches that expire on or before @Until, soonest first (INV-07)
CREATE OR ALTER PROCEDURE dbo.usp_Stock_GetNearExpiry
    @TenantId INT, @BranchId INT, @Until DATE
AS
BEGIN
    SET NOCOUNT ON;
    SELECT b.Id AS BatchId, b.ItemId, CASE WHEN i.NameEn <> N'' THEN i.NameEn ELSE i.NameAr END AS ItemName,
           b.BatchNumber, b.ExpiryDate, b.Quantity, CAST(b.Quantity * b.UnitCost AS DECIMAL(18,2)) AS StockValue, b.Status
    FROM dbo.Batches b
    JOIN dbo.Items i ON i.Id = b.ItemId
    WHERE b.TenantId = @TenantId AND b.BranchId = @BranchId AND b.Quantity > 0 AND b.ExpiryDate <= @Until
    ORDER BY b.ExpiryDate, b.Id;
END
GO
-- #endregion

-- #region usp_Stock_GetLowStock : items whose sellable stock is at or below the reorder level (AC-INV-3)
CREATE OR ALTER PROCEDURE dbo.usp_Stock_GetLowStock
    @TenantId INT, @BranchId INT, @Today DATE
AS
BEGIN
    SET NOCOUNT ON;
    SELECT i.Id AS ItemId, CASE WHEN i.NameEn <> N'' THEN i.NameEn ELSE i.NameAr END AS ItemName,
           ISNULL(s.AvailableQuantity, 0) AS AvailableQuantity, i.ReorderLevel
    FROM dbo.Items i
    OUTER APPLY (SELECT SUM(b.Quantity) AS AvailableQuantity
                 FROM dbo.Batches b
                 WHERE b.TenantId = i.TenantId AND b.ItemId = i.Id AND b.BranchId = @BranchId
                   AND b.Status = 1 AND b.ExpiryDate >= @Today) s
    WHERE i.TenantId = @TenantId AND i.IsActive = 1 AND i.ReorderLevel > 0
      AND ISNULL(s.AvailableQuantity, 0) <= i.ReorderLevel
    ORDER BY ItemName;
END
GO
-- #endregion
