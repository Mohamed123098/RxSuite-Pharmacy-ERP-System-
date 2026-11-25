/* Multi-branch and warehouse procedures (BRD 8.8): branch item settings, transfers and the HQ
   reports (stock matrix, rebalancing, branch comparison). Quantities are in the smallest unit. */

-- #region usp_BranchItemSetting_Save : MBR-02 creates or replaces one item's setting in one branch
CREATE OR ALTER PROCEDURE dbo.usp_BranchItemSetting_Save
    @TenantId INT, @BranchId INT, @ItemId INT, @SellingPrice DECIMAL(18,2) = NULL, @MinStock INT = NULL,
    @MaxStock INT = NULL, @IsHidden BIT
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.BranchItemSettings WITH (UPDLOCK, HOLDLOCK)
    SET SellingPrice = @SellingPrice, MinStock = @MinStock, MaxStock = @MaxStock, IsHidden = @IsHidden
    WHERE TenantId = @TenantId AND BranchId = @BranchId AND ItemId = @ItemId;

    IF @@ROWCOUNT = 0
        INSERT INTO dbo.BranchItemSettings (TenantId, BranchId, ItemId, SellingPrice, MinStock, MaxStock, IsHidden)
        SELECT @TenantId, b.Id, i.Id, @SellingPrice, @MinStock, @MaxStock, @IsHidden
        FROM dbo.Branches b JOIN dbo.Items i ON i.Id = @ItemId AND i.TenantId = @TenantId
        WHERE b.Id = @BranchId AND b.TenantId = @TenantId;
END
GO
-- #endregion

-- #region usp_BranchItemSetting_GetByItems : the branch's settings for several items (JSON array of Ids)
CREATE OR ALTER PROCEDURE dbo.usp_BranchItemSetting_GetByItems
    @TenantId INT, @BranchId INT, @ItemIdsJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, TenantId, BranchId, ItemId, SellingPrice, MinStock, MaxStock, IsHidden
    FROM dbo.BranchItemSettings
    WHERE TenantId = @TenantId AND BranchId = @BranchId
      AND ItemId IN (SELECT CAST(j.[value] AS INT) FROM OPENJSON(@ItemIdsJson) j);
END
GO
-- #endregion

-- #region usp_Transfer_Insert : a transfer with its lines; returns the Id
/* @LinesJson: [{ "LineNumber", "ItemId", "ItemName", "RequestedQuantity" }] */
CREATE OR ALTER PROCEDURE dbo.usp_Transfer_Insert
    @TenantId INT, @FromBranchId INT, @ToBranchId INT, @Status TINYINT, @IsDirect BIT, @Note NVARCHAR(300) = NULL,
    @RequestedBy INT, @RequestedAtUtc DATETIME2(0), @ApprovedBy INT = NULL, @ApprovedAtUtc DATETIME2(0) = NULL,
    @LinesJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.Transfers (TenantId, FromBranchId, ToBranchId, Status, IsDirect, Note, RequestedBy, RequestedAtUtc,
                               ApprovedBy, ApprovedAtUtc)
    VALUES (@TenantId, @FromBranchId, @ToBranchId, @Status, @IsDirect, @Note, @RequestedBy, @RequestedAtUtc,
            @ApprovedBy, @ApprovedAtUtc);

    DECLARE @TransferId INT = CAST(SCOPE_IDENTITY() AS INT);

    INSERT INTO dbo.TransferLines (TransferId, LineNumber, TenantId, ItemId, ItemName, RequestedQuantity)
    SELECT @TransferId, j.LineNumber, @TenantId, j.ItemId, j.ItemName, j.RequestedQuantity
    FROM OPENJSON(@LinesJson) WITH (LineNumber INT, ItemId INT, ItemName NVARCHAR(200), RequestedQuantity INT) j;

    SELECT @TransferId;
END
GO
-- #endregion

-- #region usp_Transfer_GetById : result sets 1 the transfer, 2 its lines, 3 the shipped batches
/* UPDLOCK: inside a command's transaction the transfer stays locked until it commits, so it cannot be
   shipped or received twice at the same moment. */
CREATE OR ALTER PROCEDURE dbo.usp_Transfer_GetById
    @TenantId INT, @TransferId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, TenantId, FromBranchId, ToBranchId, Status, IsDirect, Note, RequestedBy, RequestedAtUtc, ApprovedBy,
           ApprovedAtUtc, ShippedBy, ShippedAtUtc, ReceivedBy, ReceivedAtUtc, ClosingNote
    FROM dbo.Transfers WITH (UPDLOCK, ROWLOCK)
    WHERE Id = @TransferId AND TenantId = @TenantId;

    SELECT LineNumber, ItemId, ItemName, RequestedQuantity
    FROM dbo.TransferLines
    WHERE TransferId = @TransferId AND TenantId = @TenantId
    ORDER BY LineNumber;

    SELECT LineNumber, SourceBatchId, BatchNumber, ExpiryDate, UnitCost, QuantitySent, QuantityReceived, DestinationBatchId
    FROM dbo.TransferBatches
    WHERE TransferId = @TransferId AND TenantId = @TenantId
    ORDER BY LineNumber, ExpiryDate;
END
GO
-- #endregion

-- #region usp_Transfer_Update : status, who and when, and the shipped batches (added at shipping, completed at receipt)
/* @BatchesJson: [{ "LineNumber", "SourceBatchId", "BatchNumber", "ExpiryDate", "UnitCost", "QuantitySent",
                    "QuantityReceived", "DestinationBatchId" }] */
CREATE OR ALTER PROCEDURE dbo.usp_Transfer_Update
    @TenantId INT, @TransferId INT, @Status TINYINT, @ApprovedBy INT = NULL, @ApprovedAtUtc DATETIME2(0) = NULL,
    @ShippedBy INT = NULL, @ShippedAtUtc DATETIME2(0) = NULL, @ReceivedBy INT = NULL, @ReceivedAtUtc DATETIME2(0) = NULL,
    @ClosingNote NVARCHAR(300) = NULL, @BatchesJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.Transfers
    SET Status = @Status, ApprovedBy = @ApprovedBy, ApprovedAtUtc = @ApprovedAtUtc, ShippedBy = @ShippedBy,
        ShippedAtUtc = @ShippedAtUtc, ReceivedBy = @ReceivedBy, ReceivedAtUtc = @ReceivedAtUtc, ClosingNote = @ClosingNote
    WHERE Id = @TransferId AND TenantId = @TenantId;

    DECLARE @Batches TABLE (LineNumber INT, SourceBatchId INT, BatchNumber NVARCHAR(50), ExpiryDate DATE,
                            UnitCost DECIMAL(18,6), QuantitySent INT, QuantityReceived INT NULL, DestinationBatchId INT NULL);
    INSERT INTO @Batches
    SELECT j.LineNumber, j.SourceBatchId, j.BatchNumber, j.ExpiryDate, j.UnitCost, j.QuantitySent, j.QuantityReceived,
           j.DestinationBatchId
    FROM OPENJSON(@BatchesJson)
         WITH (LineNumber INT, SourceBatchId INT, BatchNumber NVARCHAR(50), ExpiryDate DATE, UnitCost DECIMAL(18,6),
               QuantitySent INT, QuantityReceived INT, DestinationBatchId INT) j;

    UPDATE tb
    SET QuantityReceived = x.QuantityReceived, DestinationBatchId = x.DestinationBatchId
    FROM dbo.TransferBatches tb
    JOIN @Batches x ON x.SourceBatchId = tb.SourceBatchId
    WHERE tb.TransferId = @TransferId AND tb.TenantId = @TenantId;

    INSERT INTO dbo.TransferBatches (TransferId, SourceBatchId, TenantId, LineNumber, BatchNumber, ExpiryDate, UnitCost,
                                     QuantitySent, QuantityReceived, DestinationBatchId)
    SELECT @TransferId, x.SourceBatchId, @TenantId, x.LineNumber, x.BatchNumber, x.ExpiryDate, x.UnitCost, x.QuantitySent,
           x.QuantityReceived, x.DestinationBatchId
    FROM @Batches x
    WHERE NOT EXISTS (SELECT 1 FROM dbo.TransferBatches tb WHERE tb.TransferId = @TransferId AND tb.SourceBatchId = x.SourceBatchId);
END
GO
-- #endregion

-- #region usp_Transfer_GetList : requests, in transit and shortages, optionally for one branch and one status
CREATE OR ALTER PROCEDURE dbo.usp_Transfer_GetList
    @TenantId INT, @BranchId INT = NULL, @Status TINYINT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (500)
           t.Id, t.FromBranchId, f.NameEn AS FromBranchName, t.ToBranchId, d.NameEn AS ToBranchName, t.Status, t.IsDirect,
           t.RequestedAtUtc, t.ShippedAtUtc, t.ReceivedAtUtc,
           (SELECT COUNT(*) FROM dbo.TransferLines l WHERE l.TransferId = t.Id) AS LineCount,
           ISNULL((SELECT SUM(tb.QuantitySent - tb.QuantityReceived) FROM dbo.TransferBatches tb
                   WHERE tb.TransferId = t.Id AND tb.QuantityReceived IS NOT NULL), 0) AS TotalShortage
    FROM dbo.Transfers t
    JOIN dbo.Branches f ON f.Id = t.FromBranchId
    JOIN dbo.Branches d ON d.Id = t.ToBranchId
    WHERE t.TenantId = @TenantId
      AND (@BranchId IS NULL OR t.FromBranchId = @BranchId OR t.ToBranchId = @BranchId)
      AND (@Status IS NULL OR t.Status = @Status)
    ORDER BY t.Id DESC;
END
GO
-- #endregion

-- #region usp_Report_StockMatrix : MBR-05 result sets 1 every batch of the matching items in every location, 2 stock in transit
CREATE OR ALTER PROCEDURE dbo.usp_Report_StockMatrix
    @TenantId INT, @Term NVARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Items TABLE (Id INT PRIMARY KEY, Name NVARCHAR(200));
    INSERT INTO @Items (Id, Name)
    SELECT TOP (50) i.Id, CASE WHEN i.NameEn = N'' THEN i.NameAr ELSE i.NameEn END
    FROM dbo.Items i
    WHERE i.TenantId = @TenantId AND i.IsActive = 1
      AND (i.Code LIKE @Term + N'%' OR i.NameEn LIKE N'%' + @Term + N'%' OR i.NameAr LIKE N'%' + @Term + N'%'
           OR i.ScientificName LIKE N'%' + @Term + N'%'
           OR EXISTS (SELECT 1 FROM dbo.ItemBarcodes bc WHERE bc.TenantId = i.TenantId AND bc.ItemId = i.Id AND bc.Barcode = @Term))
    ORDER BY i.NameEn;

    SELECT x.Id AS ItemId, x.Name AS ItemName, br.Id AS BranchId, br.NameEn AS BranchName, b.BatchNumber, b.ExpiryDate,
           b.Quantity, b.Status
    FROM @Items x
    JOIN dbo.Batches b ON b.ItemId = x.Id AND b.TenantId = @TenantId AND b.Quantity > 0
    JOIN dbo.Branches br ON br.Id = b.BranchId
    ORDER BY x.Name, br.NameEn, b.ExpiryDate;

    -- BR-MBR-1: shipped and not yet received.
    SELECT l.ItemId, t.ToBranchId, SUM(tb.QuantitySent) AS Quantity
    FROM dbo.Transfers t
    JOIN dbo.TransferBatches tb ON tb.TransferId = t.Id
    JOIN dbo.TransferLines l ON l.TransferId = t.Id AND l.LineNumber = tb.LineNumber
    WHERE t.TenantId = @TenantId AND t.Status = 3 AND tb.QuantityReceived IS NULL
      AND l.ItemId IN (SELECT Id FROM @Items)
    GROUP BY l.ItemId, t.ToBranchId;
END
GO
-- #endregion

-- #region usp_Report_RebalancingSuggestions : MBR-06 slow near-expiry or overstocked batches, next to the branch that sells the item fastest
CREATE OR ALTER PROCEDURE dbo.usp_Report_RebalancingSuggestions
    @TenantId INT, @Today DATE, @NearExpiryUntil DATE, @SalesWindowDays INT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @SinceUtc DATETIME2(0) = DATEADD(DAY, -@SalesWindowDays, CAST(@Today AS DATETIME2(0)));

    -- Units sold per item and branch in the window, net of returns.
    WITH Sales AS
    (
        SELECT s.BranchId, l.ItemId, SUM((l.Quantity - l.ReturnedQuantity) * l.BaseUnitsPerUnit) AS Units
        FROM dbo.Sales s
        JOIN dbo.SaleLines l ON l.SaleId = s.Id
        WHERE s.TenantId = @TenantId AND s.SoldAtUtc >= @SinceUtc
        GROUP BY s.BranchId, l.ItemId
    ),
    Stock AS
    (
        SELECT b.BranchId, b.ItemId, SUM(b.Quantity) AS Units
        FROM dbo.Batches b
        WHERE b.TenantId = @TenantId AND b.Status = 1 AND b.ExpiryDate >= @Today
        GROUP BY b.BranchId, b.ItemId
    ),
    Candidates AS
    (
        SELECT b.Id AS BatchId, b.BranchId, b.ItemId, b.BatchNumber, b.ExpiryDate, b.Quantity,
               CASE WHEN b.ExpiryDate <= @NearExpiryUntil THEN N'NearExpiry' ELSE N'Overstock' END AS Reason
        FROM dbo.Batches b
        JOIN Stock st ON st.BranchId = b.BranchId AND st.ItemId = b.ItemId
        LEFT JOIN dbo.BranchItemSettings bs ON bs.TenantId = @TenantId AND bs.BranchId = b.BranchId AND bs.ItemId = b.ItemId
        WHERE b.TenantId = @TenantId AND b.Status = 1 AND b.Quantity > 0 AND b.ExpiryDate >= @Today
          AND (b.ExpiryDate <= @NearExpiryUntil OR (bs.MaxStock IS NOT NULL AND st.Units > bs.MaxStock))
    )
    SELECT c.ItemId, CASE WHEN i.NameEn = N'' THEN i.NameAr ELSE i.NameEn END AS ItemName,
           c.BranchId AS FromBranchId, fb.NameEn AS FromBranchName, c.BatchNumber, c.ExpiryDate,
           c.Quantity AS AvailableQuantity, ISNULL(fs.Units, 0) AS FromBranchSales,
           t.BranchId AS ToBranchId, tb.NameEn AS ToBranchName, t.Units AS ToBranchSales, c.Reason,
           CASE WHEN t.Units < c.Quantity THEN t.Units ELSE c.Quantity END AS SuggestedQuantity
    FROM Candidates c
    JOIN dbo.Items i ON i.Id = c.ItemId
    JOIN dbo.Branches fb ON fb.Id = c.BranchId
    LEFT JOIN Sales fs ON fs.BranchId = c.BranchId AND fs.ItemId = c.ItemId
    CROSS APPLY (SELECT TOP (1) s.BranchId, s.Units FROM Sales s
                 JOIN dbo.Branches b2 ON b2.Id = s.BranchId AND b2.IsActive = 1
                 LEFT JOIN dbo.BranchItemSettings h ON h.TenantId = @TenantId AND h.BranchId = s.BranchId AND h.ItemId = s.ItemId
                 WHERE s.ItemId = c.ItemId AND s.BranchId <> c.BranchId AND s.Units > ISNULL(fs.Units, 0)
                   AND ISNULL(h.IsHidden, 0) = 0
                 ORDER BY s.Units DESC) t
    JOIN dbo.Branches tb ON tb.Id = t.BranchId
    ORDER BY c.ExpiryDate, ItemName;
END
GO
-- #endregion

-- #region usp_Report_BranchPerformance : MBR-08 sales, cost, stock value and expiry losses per branch between two Cairo dates
/* Cost of sales and expiry losses are valued at each batch's current unit cost. */
CREATE OR ALTER PROCEDURE dbo.usp_Report_BranchPerformance
    @TenantId INT, @From DATE, @To DATE
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @FromUtc DATETIME2(0) = CAST(CAST(@From AS DATETIME2(0)) AT TIME ZONE 'Egypt Standard Time' AT TIME ZONE 'UTC' AS DATETIME2(0));
    DECLARE @ToUtc   DATETIME2(0) = CAST(CAST(DATEADD(DAY, 1, @To) AS DATETIME2(0)) AT TIME ZONE 'Egypt Standard Time' AT TIME ZONE 'UTC' AS DATETIME2(0));

    SELECT br.Id AS BranchId, br.NameEn AS BranchName,
           ISNULL(s.SalesTotal, 0) AS SalesTotal, ISNULL(s.VatTotal, 0) AS VatTotal, ISNULL(r.ReturnsTotal, 0) AS ReturnsTotal,
           ISNULL(m.CostOfSales, 0) AS CostOfSales, ISNULL(v.StockValue, 0) AS StockValue,
           ISNULL(m.ExpiryLosses, 0) AS ExpiryLosses, DATEDIFF(DAY, @From, @To) + 1 AS Days
    FROM dbo.Branches br
    OUTER APPLY (SELECT SUM(x.Total) AS SalesTotal, SUM(x.VatTotal) AS VatTotal FROM dbo.Sales x
                 WHERE x.TenantId = @TenantId AND x.BranchId = br.Id AND x.SoldAtUtc >= @FromUtc AND x.SoldAtUtc < @ToUtc) s
    OUTER APPLY (SELECT SUM(x.RefundTotal) AS ReturnsTotal FROM dbo.SaleReturns x
                 WHERE x.TenantId = @TenantId AND x.BranchId = br.Id AND x.ReturnedAtUtc >= @FromUtc AND x.ReturnedAtUtc < @ToUtc) r
    OUTER APPLY (SELECT
                   -- Sales out minus restocked returns, at cost.
                   SUM(CASE WHEN sm.Type IN (2, 3) THEN -sm.Quantity * b.UnitCost ELSE 0 END) AS CostOfSales,
                   SUM(CASE WHEN sm.Type = 4 AND sm.Quantity < 0 AND sm.Reason LIKE N'Expiry%' THEN -sm.Quantity * b.UnitCost ELSE 0 END) AS ExpiryLosses
                 FROM dbo.StockMovements sm
                 JOIN dbo.Batches b ON b.Id = sm.BatchId
                 WHERE sm.TenantId = @TenantId AND sm.BranchId = br.Id AND sm.CreatedAtUtc >= @FromUtc AND sm.CreatedAtUtc < @ToUtc) m
    OUTER APPLY (SELECT SUM(b.Quantity * b.UnitCost) AS StockValue FROM dbo.Batches b
                 WHERE b.TenantId = @TenantId AND b.BranchId = br.Id) v
    WHERE br.TenantId = @TenantId AND br.IsActive = 1
    ORDER BY br.NameEn;
END
GO
-- #endregion
