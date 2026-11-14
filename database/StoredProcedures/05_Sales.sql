/* Point-of-sale procedures (BRD 8.3): shifts, receipts and returns. */

-- #region usp_Shift_Insert : opens a shift and returns its Id
CREATE OR ALTER PROCEDURE dbo.usp_Shift_Insert
    @TenantId INT, @BranchId INT, @CashierId INT, @TerminalCode NVARCHAR(30), @OpeningFloat DECIMAL(18,2),
    @OpenedAtUtc DATETIME2(0)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.Shifts (TenantId, BranchId, CashierId, TerminalCode, OpeningFloat, Status, OpenedAtUtc)
    VALUES (@TenantId, @BranchId, @CashierId, @TerminalCode, @OpeningFloat, 1, @OpenedAtUtc);

    SELECT CAST(SCOPE_IDENTITY() AS INT);
END
GO
-- #endregion

-- #region usp_Shift_GetOpen : the cashier's open shift on a branch, or no row
CREATE OR ALTER PROCEDURE dbo.usp_Shift_GetOpen
    @TenantId INT, @BranchId INT, @CashierId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, TenantId, BranchId, CashierId, TerminalCode, OpeningFloat, Status, OpenedAtUtc, ClosedAtUtc,
           ExpectedCash, CountedCash, Variance, ClosingNote, ApprovedBy
    FROM dbo.Shifts
    WHERE TenantId = @TenantId AND BranchId = @BranchId AND CashierId = @CashierId AND Status = 1;
END
GO
-- #endregion

-- #region usp_Shift_GetById : one shift of the tenant, or no row
CREATE OR ALTER PROCEDURE dbo.usp_Shift_GetById
    @TenantId INT, @ShiftId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, TenantId, BranchId, CashierId, TerminalCode, OpeningFloat, Status, OpenedAtUtc, ClosedAtUtc,
           ExpectedCash, CountedCash, Variance, ClosingNote, ApprovedBy
    FROM dbo.Shifts
    WHERE Id = @ShiftId AND TenantId = @TenantId;
END
GO
-- #endregion

-- #region usp_Shift_GetCashTotals : the cash that went through the drawer during a shift (X / Z report)
CREATE OR ALTER PROCEDURE dbo.usp_Shift_GetCashTotals
    @TenantId INT, @ShiftId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        (SELECT ISNULL(SUM(p.Amount), 0)
         FROM dbo.SalePayments p
         JOIN dbo.Sales s ON s.Id = p.SaleId
         WHERE s.TenantId = @TenantId AND s.ShiftId = @ShiftId AND p.Method = 1) AS CashSales,
        (SELECT ISNULL(SUM(r.RefundTotal), 0)
         FROM dbo.SaleReturns r
         WHERE r.TenantId = @TenantId AND r.ShiftId = @ShiftId AND r.RefundMethod = 1) AS CashRefunds,
        (SELECT ISNULL(SUM(c.Amount), 0)
         FROM dbo.CashMovements c
         WHERE c.TenantId = @TenantId AND c.ShiftId = @ShiftId AND c.Type = 1) AS PayIns,
        (SELECT ISNULL(SUM(c.Amount), 0)
         FROM dbo.CashMovements c
         WHERE c.TenantId = @TenantId AND c.ShiftId = @ShiftId AND c.Type = 2) AS PayOuts;
END
GO
-- #endregion

-- #region usp_CashMovement_Insert : a pay-in or pay-out with its reason
CREATE OR ALTER PROCEDURE dbo.usp_CashMovement_Insert
    @TenantId INT, @UserId INT, @ShiftId INT, @Type TINYINT, @Amount DECIMAL(18,2), @Reason NVARCHAR(300)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.CashMovements (TenantId, ShiftId, Type, Amount, Reason, UserId)
    VALUES (@TenantId, @ShiftId, @Type, @Amount, @Reason, @UserId);
END
GO
-- #endregion

-- #region usp_Shift_Close : closes an open shift with the counted cash and the variance
CREATE OR ALTER PROCEDURE dbo.usp_Shift_Close
    @TenantId INT, @ShiftId INT, @ExpectedCash DECIMAL(18,2), @CountedCash DECIMAL(18,2), @Variance DECIMAL(18,2),
    @ClosingNote NVARCHAR(300) = NULL, @ApprovedBy INT = NULL, @ClosedAtUtc DATETIME2(0)
AS
BEGIN
    SET NOCOUNT ON;
    -- "AND Status = 1": closing twice changes nothing the second time.
    UPDATE dbo.Shifts
    SET Status = 2, ExpectedCash = @ExpectedCash, CountedCash = @CountedCash, Variance = @Variance,
        ClosingNote = @ClosingNote, ApprovedBy = @ApprovedBy, ClosedAtUtc = @ClosedAtUtc
    WHERE Id = @ShiftId AND TenantId = @TenantId AND Status = 1;
END
GO
-- #endregion

-- #region usp_Sale_GetIdByOfflineId : the receipt already saved for a terminal's offline Id, or no row (BR-POS-5)
CREATE OR ALTER PROCEDURE dbo.usp_Sale_GetIdByOfflineId
    @TenantId INT, @OfflineId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id FROM dbo.Sales WHERE TenantId = @TenantId AND OfflineId = @OfflineId;
END
GO
-- #endregion

-- #region usp_Sale_Insert : saves a whole receipt (header, lines, batch allocations, payments); returns its Id
/* @LinesJson:       [{ "LineNumber", "ItemId", "ItemName", "UnitType", "Quantity", "BaseUnitsPerUnit", "UnitPrice",
                        "DiscountPercent", "VatRate", "LineTotal", "VatAmount" }]
   @AllocationsJson: [{ "LineNumber", "BatchId", "Quantity" }]   which batch each line took stock from (FEFO)
   @PaymentsJson:    [{ "Method", "Amount", "Reference" }]
   Stock is reduced separately by usp_Stock_ApplyMovements in the same transaction. */
CREATE OR ALTER PROCEDURE dbo.usp_Sale_Insert
    @TenantId INT, @BranchId INT, @ShiftId INT, @CashierId INT, @PharmacistId INT = NULL,
    @OfflineId UNIQUEIDENTIFIER = NULL, @BuyerName NVARCHAR(150) = NULL, @BuyerNationalId NVARCHAR(20) = NULL,
    @BuyerTaxNumber NVARCHAR(30) = NULL, @InvoiceDiscountPercent DECIMAL(5,2), @Total DECIMAL(18,2),
    @VatTotal DECIMAL(18,2), @ChangeGiven DECIMAL(18,2), @SoldAtUtc DATETIME2(0),
    @LinesJson NVARCHAR(MAX), @AllocationsJson NVARCHAR(MAX), @PaymentsJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO dbo.Sales (TenantId, BranchId, ShiftId, CashierId, PharmacistId, OfflineId, BuyerName, BuyerNationalId,
                           BuyerTaxNumber, InvoiceDiscountPercent, Total, VatTotal, ChangeGiven, SoldAtUtc)
    VALUES (@TenantId, @BranchId, @ShiftId, @CashierId, @PharmacistId, @OfflineId, @BuyerName, @BuyerNationalId,
            @BuyerTaxNumber, @InvoiceDiscountPercent, @Total, @VatTotal, @ChangeGiven, @SoldAtUtc);

    DECLARE @SaleId BIGINT = CAST(SCOPE_IDENTITY() AS BIGINT);

    INSERT INTO dbo.SaleLines (SaleId, LineNumber, TenantId, ItemId, ItemName, UnitType, Quantity, BaseUnitsPerUnit,
                               UnitPrice, DiscountPercent, VatRate, LineTotal, VatAmount)
    SELECT @SaleId, j.LineNumber, @TenantId, j.ItemId, j.ItemName, j.UnitType, j.Quantity, j.BaseUnitsPerUnit,
           j.UnitPrice, j.DiscountPercent, j.VatRate, j.LineTotal, j.VatAmount
    FROM OPENJSON(@LinesJson)
         WITH (LineNumber INT, ItemId INT, ItemName NVARCHAR(200), UnitType TINYINT, Quantity INT, BaseUnitsPerUnit INT,
               UnitPrice DECIMAL(18,2), DiscountPercent DECIMAL(5,2), VatRate DECIMAL(5,4), LineTotal DECIMAL(18,2),
               VatAmount DECIMAL(18,2)) j;

    INSERT INTO dbo.SaleLineBatches (SaleId, LineNumber, BatchId, TenantId, Quantity)
    SELECT @SaleId, j.LineNumber, j.BatchId, @TenantId, j.Quantity
    FROM OPENJSON(@AllocationsJson) WITH (LineNumber INT, BatchId INT, Quantity INT) j;

    INSERT INTO dbo.SalePayments (SaleId, TenantId, Method, Amount, Reference)
    SELECT @SaleId, @TenantId, j.Method, j.Amount, j.Reference
    FROM OPENJSON(@PaymentsJson) WITH (Method TINYINT, Amount DECIMAL(18,2), Reference NVARCHAR(100)) j;

    SELECT @SaleId;
END
GO
-- #endregion

-- #region usp_Sale_GetById : result sets 1 = receipt, 2 = lines, 3 = batch allocations, 4 = payments
CREATE OR ALTER PROCEDURE dbo.usp_Sale_GetById
    @TenantId INT, @SaleId BIGINT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Id, TenantId, BranchId, ShiftId, CashierId, PharmacistId, OfflineId, BuyerName, BuyerNationalId,
           BuyerTaxNumber, InvoiceDiscountPercent, Total, VatTotal, ChangeGiven, SoldAtUtc
    FROM dbo.Sales
    WHERE Id = @SaleId AND TenantId = @TenantId;

    SELECT LineNumber, ItemId, ItemName, UnitType, Quantity, BaseUnitsPerUnit, UnitPrice, DiscountPercent, VatRate,
           LineTotal, VatAmount, ReturnedQuantity
    FROM dbo.SaleLines
    WHERE SaleId = @SaleId AND TenantId = @TenantId
    ORDER BY LineNumber;

    SELECT LineNumber, BatchId, Quantity, ReturnedQuantity
    FROM dbo.SaleLineBatches
    WHERE SaleId = @SaleId AND TenantId = @TenantId
    ORDER BY LineNumber, BatchId;

    SELECT Method, Amount, Reference
    FROM dbo.SalePayments
    WHERE SaleId = @SaleId AND TenantId = @TenantId
    ORDER BY Id;
END
GO
-- #endregion

-- #region usp_SaleReturn_Insert : saves a return and marks the returned quantities on the receipt; returns its Id
/* @LinesJson:        [{ "LineNumber", "Quantity", "IsResaleable", "RefundAmount" }]
   @RestorationsJson: [{ "LineNumber", "BatchId", "Quantity" }]   smallest units going back per batch
   The CHECK constraints on SaleLines / SaleLineBatches stop a line being returned twice over,
   even when two returns for the same receipt arrive at the same moment. */
CREATE OR ALTER PROCEDURE dbo.usp_SaleReturn_Insert
    @TenantId INT, @SaleId BIGINT, @BranchId INT, @ShiftId INT, @CashierId INT, @RefundMethod TINYINT,
    @Reason NVARCHAR(300), @RefundTotal DECIMAL(18,2), @ReturnedAtUtc DATETIME2(0),
    @LinesJson NVARCHAR(MAX), @RestorationsJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO dbo.SaleReturns (TenantId, SaleId, BranchId, ShiftId, CashierId, RefundMethod, Reason, RefundTotal, ReturnedAtUtc)
    VALUES (@TenantId, @SaleId, @BranchId, @ShiftId, @CashierId, @RefundMethod, @Reason, @RefundTotal, @ReturnedAtUtc);

    DECLARE @SaleReturnId BIGINT = CAST(SCOPE_IDENTITY() AS BIGINT);

    INSERT INTO dbo.SaleReturnLines (SaleReturnId, TenantId, LineNumber, Quantity, IsResaleable, RefundAmount)
    SELECT @SaleReturnId, @TenantId, j.LineNumber, j.Quantity, j.IsResaleable, j.RefundAmount
    FROM OPENJSON(@LinesJson) WITH (LineNumber INT, Quantity INT, IsResaleable BIT, RefundAmount DECIMAL(18,2)) j;

    UPDATE l
    SET l.ReturnedQuantity = l.ReturnedQuantity + j.Quantity
    FROM dbo.SaleLines l
    JOIN OPENJSON(@LinesJson) WITH (LineNumber INT, Quantity INT) j ON j.LineNumber = l.LineNumber
    WHERE l.SaleId = @SaleId AND l.TenantId = @TenantId;

    UPDATE a
    SET a.ReturnedQuantity = a.ReturnedQuantity + j.Quantity
    FROM dbo.SaleLineBatches a
    JOIN (SELECT LineNumber, BatchId, SUM(Quantity) AS Quantity
          FROM OPENJSON(@RestorationsJson) WITH (LineNumber INT, BatchId INT, Quantity INT)
          GROUP BY LineNumber, BatchId) j ON j.LineNumber = a.LineNumber AND j.BatchId = a.BatchId
    WHERE a.SaleId = @SaleId AND a.TenantId = @TenantId;

    SELECT @SaleReturnId;
END
GO
-- #endregion
