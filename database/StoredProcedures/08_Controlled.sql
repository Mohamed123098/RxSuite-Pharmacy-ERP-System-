/* Controlled and narcotic drugs register (BRD 8.7). Entries are append-only: there is no update or
   delete procedure, and TR_ControlledRegister_Immutable rejects both (BR-CTL-2).
   Register dates are filtered as Cairo calendar days (NFR-09); entries are stored in UTC. */

-- #region usp_ControlledRegister_Insert : appends entries with their running balance (CTL-04); returns 1, or 0 when a balance would go below zero
/* @EntriesJson: [{ "BranchId", "ItemId", "BatchId", "Type", "Quantity", "OccurredAtUtc", "DocumentType", "DocumentId",
                    "PatientId", "PatientName", "PatientNationalId", "PrescriberName", "PrescriberLicence",
                    "PrescriptionId", "PrescriptionNumber", "PrescriptionDate", "PrescriptionImageId",
                    "PharmacistId", "CorrectsEntryId", "Note" }]
   The latest balance of each item is read WITH (UPDLOCK, HOLDLOCK): a second pharmacist writing to the
   same item waits until this transaction ends, so two entries never start from the same balance. */
CREATE OR ALTER PROCEDURE dbo.usp_ControlledRegister_Insert
    @TenantId INT, @EntriesJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Rows TABLE
    (
        Ordinal INT NOT NULL PRIMARY KEY, BranchId INT NOT NULL, ItemId INT NOT NULL, BatchId INT NULL, Type TINYINT NOT NULL,
        Quantity INT NOT NULL, OccurredAtUtc DATETIME2(0) NOT NULL, DocumentType NVARCHAR(30) NOT NULL, DocumentId BIGINT NOT NULL,
        PatientId INT NULL, PatientName NVARCHAR(150) NULL, PatientNationalId NVARCHAR(14) NULL, PrescriberName NVARCHAR(150) NULL,
        PrescriberLicence NVARCHAR(50) NULL, PrescriptionId INT NULL, PrescriptionNumber NVARCHAR(50) NULL,
        PrescriptionDate DATE NULL, PrescriptionImageId INT NULL, PharmacistId INT NOT NULL, CorrectsEntryId BIGINT NULL,
        Note NVARCHAR(500) NULL, Balance INT NULL
    );

    INSERT INTO @Rows (Ordinal, BranchId, ItemId, BatchId, Type, Quantity, OccurredAtUtc, DocumentType, DocumentId, PatientId,
                       PatientName, PatientNationalId, PrescriberName, PrescriberLicence, PrescriptionId, PrescriptionNumber,
                       PrescriptionDate, PrescriptionImageId, PharmacistId, CorrectsEntryId, Note)
    SELECT CAST(o.[key] AS INT), j.BranchId, j.ItemId, j.BatchId, j.Type, j.Quantity, j.OccurredAtUtc, j.DocumentType,
           j.DocumentId, j.PatientId, j.PatientName, j.PatientNationalId, j.PrescriberName, j.PrescriberLicence,
           j.PrescriptionId, j.PrescriptionNumber, j.PrescriptionDate, j.PrescriptionImageId, j.PharmacistId,
           j.CorrectsEntryId, j.Note
    FROM OPENJSON(@EntriesJson) o
    CROSS APPLY OPENJSON(o.[value])
         WITH (BranchId INT, ItemId INT, BatchId INT, Type TINYINT, Quantity INT, OccurredAtUtc DATETIME2(0),
               DocumentType NVARCHAR(30), DocumentId BIGINT, PatientId INT, PatientName NVARCHAR(150),
               PatientNationalId NVARCHAR(14), PrescriberName NVARCHAR(150), PrescriberLicence NVARCHAR(50),
               PrescriptionId INT, PrescriptionNumber NVARCHAR(50), PrescriptionDate DATE, PrescriptionImageId INT,
               PharmacistId INT, CorrectsEntryId BIGINT, Note NVARCHAR(500)) j;

    -- A correction must point to an entry of the same tenant, branch and item (CTL-05).
    IF EXISTS (SELECT 1 FROM @Rows r
               WHERE r.CorrectsEntryId IS NOT NULL
                 AND NOT EXISTS (SELECT 1 FROM dbo.ControlledRegister c
                                 WHERE c.Id = r.CorrectsEntryId AND c.TenantId = @TenantId
                                   AND c.BranchId = r.BranchId AND c.ItemId = r.ItemId))
        THROW 51003, 'A correction must reference an entry of the same branch and item.', 1;

    -- Running balance: last balance on record + the entries of this call, in order.
    UPDATE r
    SET Balance = ISNULL(p.Balance, 0)
                  + (SELECT SUM(x.Quantity) FROM @Rows x
                     WHERE x.BranchId = r.BranchId AND x.ItemId = r.ItemId AND x.Ordinal <= r.Ordinal)
    FROM @Rows r
    OUTER APPLY (SELECT TOP (1) c.Balance
                 FROM dbo.ControlledRegister c WITH (UPDLOCK, HOLDLOCK)
                 WHERE c.TenantId = @TenantId AND c.BranchId = r.BranchId AND c.ItemId = r.ItemId
                 ORDER BY c.Id DESC) p;

    IF EXISTS (SELECT 1 FROM @Rows WHERE Balance < 0)
    BEGIN
        SELECT CAST(0 AS BIT);
        RETURN;
    END

    -- ORDER BY: identity values follow the order of the entries, so "latest" stays well defined.
    INSERT INTO dbo.ControlledRegister (TenantId, BranchId, ItemId, BatchId, Type, Quantity, Balance, OccurredAtUtc,
                                        DocumentType, DocumentId, PatientId, PatientName, PatientNationalId,
                                        PrescriberName, PrescriberLicence, PrescriptionId, PrescriptionNumber,
                                        PrescriptionDate, PrescriptionImageId, PharmacistId, CorrectsEntryId, Note)
    SELECT @TenantId, BranchId, ItemId, BatchId, Type, Quantity, Balance, OccurredAtUtc, DocumentType, DocumentId,
           PatientId, PatientName, PatientNationalId, PrescriberName, PrescriberLicence, PrescriptionId,
           PrescriptionNumber, PrescriptionDate, PrescriptionImageId, PharmacistId, CorrectsEntryId, Note
    FROM @Rows
    ORDER BY Ordinal;

    SELECT CAST(1 AS BIT);
END
GO
-- #endregion

-- #region usp_ControlledRegister_GetById : one entry of the tenant, or no row
CREATE OR ALTER PROCEDURE dbo.usp_ControlledRegister_GetById
    @TenantId INT, @EntryId BIGINT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, TenantId, BranchId, ItemId, BatchId, Type, Quantity, Balance, OccurredAtUtc, DocumentType, DocumentId,
           PatientId, PatientName, PatientNationalId, PrescriberName, PrescriberLicence, PrescriptionId,
           PrescriptionNumber, PrescriptionDate, PrescriptionImageId, PharmacistId, CorrectsEntryId, Note
    FROM dbo.ControlledRegister
    WHERE Id = @EntryId AND TenantId = @TenantId;
END
GO
-- #endregion

-- #region usp_ControlledRegister_GetBalance : the running balance of an item in a branch (0 before the first entry)
CREATE OR ALTER PROCEDURE dbo.usp_ControlledRegister_GetBalance
    @TenantId INT, @BranchId INT, @ItemId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT ISNULL((SELECT TOP (1) Balance FROM dbo.ControlledRegister
                   WHERE TenantId = @TenantId AND BranchId = @BranchId AND ItemId = @ItemId
                   ORDER BY Id DESC), 0);
END
GO
-- #endregion

-- #region usp_ControlledRegister_GetStockQuantity : physical stock of an item in a branch, every batch status included
CREATE OR ALTER PROCEDURE dbo.usp_ControlledRegister_GetStockQuantity
    @TenantId INT, @BranchId INT, @ItemId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT ISNULL(SUM(Quantity), 0) FROM dbo.Batches
    WHERE TenantId = @TenantId AND BranchId = @BranchId AND ItemId = @ItemId;
END
GO
-- #endregion

-- #region usp_ControlledRegister_GetDispensedToPatient : CTL-08 units of an item a patient received since a date, net of corrections
CREATE OR ALTER PROCEDURE dbo.usp_ControlledRegister_GetDispensedToPatient
    @TenantId INT, @PatientId INT, @ItemId INT, @SinceUtc DATETIME2(0)
AS
BEGIN
    SET NOCOUNT ON;
    -- Dispense entries are negative; a correction of one of them adjusts it. The total is returned positive.
    SELECT ISNULL(-SUM(x.Quantity), 0)
    FROM (SELECT c.Quantity FROM dbo.ControlledRegister c
          WHERE c.TenantId = @TenantId AND c.PatientId = @PatientId AND c.ItemId = @ItemId
            AND c.Type = 2 AND c.OccurredAtUtc >= @SinceUtc
          UNION ALL
          SELECT k.Quantity FROM dbo.ControlledRegister k
          JOIN dbo.ControlledRegister c ON c.Id = k.CorrectsEntryId
          WHERE k.TenantId = @TenantId AND c.PatientId = @PatientId AND c.ItemId = @ItemId
            AND c.Type = 2 AND c.OccurredAtUtc >= @SinceUtc) x;
END
GO
-- #endregion

-- #region usp_ControlledRegister_GetEntries : CTL-04 / CTL-07 the register of one item between two Cairo dates, oldest first
CREATE OR ALTER PROCEDURE dbo.usp_ControlledRegister_GetEntries
    @TenantId INT, @BranchId INT, @ItemId INT, @From DATE, @To DATE
AS
BEGIN
    SET NOCOUNT ON;
    -- Cairo midnight of @From up to Cairo midnight after @To, as UTC.
    DECLARE @FromUtc DATETIME2(0) = CAST(CAST(@From AS DATETIME2(0)) AT TIME ZONE 'Egypt Standard Time' AT TIME ZONE 'UTC' AS DATETIME2(0));
    DECLARE @ToUtc   DATETIME2(0) = CAST(CAST(DATEADD(DAY, 1, @To) AS DATETIME2(0)) AT TIME ZONE 'Egypt Standard Time' AT TIME ZONE 'UTC' AS DATETIME2(0));

    SELECT c.Id, c.OccurredAtUtc, c.Type, c.DocumentType, c.DocumentId, b.BatchNumber,
           CASE WHEN c.Quantity > 0 THEN c.Quantity ELSE 0 END AS QuantityIn,
           CASE WHEN c.Quantity < 0 THEN -c.Quantity ELSE 0 END AS QuantityOut,
           c.Balance, c.PatientName, c.PatientNationalId, c.PrescriberName, c.PrescriberLicence, c.PrescriptionNumber,
           c.PrescriptionDate, c.PrescriptionImageId, u.FullName AS PharmacistName, c.CorrectsEntryId, c.Note
    FROM dbo.ControlledRegister c
    JOIN dbo.Users u ON u.Id = c.PharmacistId
    LEFT JOIN dbo.Batches b ON b.Id = c.BatchId
    WHERE c.TenantId = @TenantId AND c.BranchId = @BranchId AND c.ItemId = @ItemId
      AND c.OccurredAtUtc >= @FromUtc AND c.OccurredAtUtc < @ToUtc
    ORDER BY c.Id;
END
GO
-- #endregion

-- #region usp_ControlledRegister_GetMonthlySummary : CTL-07 opening, in, out and closing per item for a Cairo calendar month
CREATE OR ALTER PROCEDURE dbo.usp_ControlledRegister_GetMonthlySummary
    @TenantId INT, @BranchId INT, @Year INT, @Month INT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @First DATE = DATEFROMPARTS(@Year, @Month, 1);
    DECLARE @FromUtc DATETIME2(0) = CAST(CAST(@First AS DATETIME2(0)) AT TIME ZONE 'Egypt Standard Time' AT TIME ZONE 'UTC' AS DATETIME2(0));
    DECLARE @ToUtc   DATETIME2(0) = CAST(CAST(DATEADD(MONTH, 1, @First) AS DATETIME2(0)) AT TIME ZONE 'Egypt Standard Time' AT TIME ZONE 'UTC' AS DATETIME2(0));

    SELECT s.ItemId, CASE WHEN i.NameEn = N'' THEN i.NameAr ELSE i.NameEn END AS ItemName,
           s.OpeningBalance, s.TotalIn, s.TotalOut, s.OpeningBalance + s.TotalIn - s.TotalOut AS ClosingBalance
    FROM (SELECT c.ItemId,
                 SUM(CASE WHEN c.OccurredAtUtc < @FromUtc THEN c.Quantity ELSE 0 END) AS OpeningBalance,
                 SUM(CASE WHEN c.OccurredAtUtc >= @FromUtc AND c.Quantity > 0 THEN c.Quantity ELSE 0 END) AS TotalIn,
                 SUM(CASE WHEN c.OccurredAtUtc >= @FromUtc AND c.Quantity < 0 THEN -c.Quantity ELSE 0 END) AS TotalOut
          FROM dbo.ControlledRegister c
          WHERE c.TenantId = @TenantId AND c.BranchId = @BranchId AND c.OccurredAtUtc < @ToUtc
          GROUP BY c.ItemId) s
    JOIN dbo.Items i ON i.Id = s.ItemId
    ORDER BY ItemName;
END
GO
-- #endregion

-- #region usp_ControlledBalanceCheck_Insert : CTL-06 a signed count; returns the Id
CREATE OR ALTER PROCEDURE dbo.usp_ControlledBalanceCheck_Insert
    @TenantId INT, @BranchId INT, @ItemId INT, @CountedQuantity INT, @RegisterBalance INT, @StockQuantity INT,
    @IsMismatch BIT, @PharmacistId INT, @Note NVARCHAR(500) = NULL, @CheckedAtUtc DATETIME2(0)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.ControlledBalanceChecks (TenantId, BranchId, ItemId, CountedQuantity, RegisterBalance, StockQuantity,
                                             IsMismatch, PharmacistId, Note, CheckedAtUtc)
    VALUES (@TenantId, @BranchId, @ItemId, @CountedQuantity, @RegisterBalance, @StockQuantity,
            @IsMismatch, @PharmacistId, @Note, @CheckedAtUtc);

    SELECT CAST(SCOPE_IDENTITY() AS INT);
END
GO
-- #endregion

-- #region usp_ControlledRegister_GetStatus : US-CTL-3 register balance, stock and last check of every controlled item in a branch
CREATE OR ALTER PROCEDURE dbo.usp_ControlledRegister_GetStatus
    @TenantId INT, @BranchId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT i.Id AS ItemId, CASE WHEN i.NameEn = N'' THEN i.NameAr ELSE i.NameEn END AS ItemName,
           ISNULL(r.Balance, 0) AS RegisterBalance, ISNULL(s.Quantity, 0) AS StockQuantity,
           k.CheckedAtUtc AS LastCheckAtUtc, k.CountedQuantity AS LastCheckCounted,
           ISNULL(k.IsMismatch, CAST(0 AS BIT)) AS LastCheckMismatch
    FROM dbo.Items i
    OUTER APPLY (SELECT TOP (1) c.Balance FROM dbo.ControlledRegister c
                 WHERE c.TenantId = @TenantId AND c.BranchId = @BranchId AND c.ItemId = i.Id
                 ORDER BY c.Id DESC) r
    OUTER APPLY (SELECT SUM(b.Quantity) AS Quantity FROM dbo.Batches b
                 WHERE b.TenantId = @TenantId AND b.BranchId = @BranchId AND b.ItemId = i.Id) s
    OUTER APPLY (SELECT TOP (1) x.CheckedAtUtc, x.CountedQuantity, x.IsMismatch FROM dbo.ControlledBalanceChecks x
                 WHERE x.TenantId = @TenantId AND x.BranchId = @BranchId AND x.ItemId = i.Id
                 ORDER BY x.CheckedAtUtc DESC, x.Id DESC) k
    WHERE i.TenantId = @TenantId AND i.ControlSchedule > 0 AND i.IsActive = 1
    ORDER BY ItemName;
END
GO
-- #endregion
