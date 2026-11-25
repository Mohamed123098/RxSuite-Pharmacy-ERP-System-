/* Prescription procedures (BRD 8.6): the central drug database, patients, prescribers,
   prescriptions with their images, and the dispense record. Quantities are in the smallest unit. */

-- #region usp_Drug_GetById : one medicine of the central database (no tenant: the database is shared)
CREATE OR ALTER PROCEDURE dbo.usp_Drug_GetById
    @DrugId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT d.Id, d.TradeName, d.Strength, d.DosageForm, d.AtcCode, d.Manufacturer, d.EdaRegistrationNumber,
           d.OfficialPrice, d.IsRxOnly, d.ControlSchedule, d.PregnancyCategory,
           ISNULL((SELECT STRING_AGG(g.Name + ISNULL(N' ' + FORMAT(di.StrengthMgPerUnit, N'0.###') + N' mg', N''), N' + ')
                   FROM dbo.DrugIngredients di JOIN dbo.Ingredients g ON g.Code = di.IngredientCode
                   WHERE di.DrugId = d.Id), N'') AS Ingredients
    FROM dbo.Drugs d
    WHERE d.Id = @DrugId;
END
GO
-- #endregion

-- #region usp_Drug_Search : RX-01 search by trade name or ingredient (no tenant: the database is shared)
CREATE OR ALTER PROCEDURE dbo.usp_Drug_Search
    @Term NVARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (50)
           d.Id, d.TradeName, d.Strength, d.DosageForm, d.AtcCode, d.Manufacturer, d.EdaRegistrationNumber,
           d.OfficialPrice, d.IsRxOnly, d.ControlSchedule, d.PregnancyCategory,
           ISNULL((SELECT STRING_AGG(g.Name + ISNULL(N' ' + FORMAT(di.StrengthMgPerUnit, N'0.###') + N' mg', N''), N' + ')
                   FROM dbo.DrugIngredients di JOIN dbo.Ingredients g ON g.Code = di.IngredientCode
                   WHERE di.DrugId = d.Id), N'') AS Ingredients
    FROM dbo.Drugs d
    WHERE d.TradeName LIKE N'%' + @Term + N'%'
       OR EXISTS (SELECT 1 FROM dbo.DrugIngredients di JOIN dbo.Ingredients g ON g.Code = di.IngredientCode
                  WHERE di.DrugId = d.Id AND g.Name LIKE N'%' + @Term + N'%')
    ORDER BY d.TradeName;
END
GO
-- #endregion

-- #region usp_Drug_GetClinicalProfiles : the ingredients behind the tenant's items (one row per item and ingredient)
CREATE OR ALTER PROCEDURE dbo.usp_Drug_GetClinicalProfiles
    @TenantId INT, @ItemIdsJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT i.Id AS ItemId,
           CASE WHEN i.NameEn = N'' THEN i.NameAr ELSE i.NameEn END AS ItemName,
           g.Code, g.Name, g.ClassCode, di.StrengthMgPerUnit, g.MaxDailyDoseMg, g.MinAgeYears
    FROM dbo.Items i
    JOIN dbo.DrugIngredients di ON di.DrugId = i.DrugId
    JOIN dbo.Ingredients g ON g.Code = di.IngredientCode
    WHERE i.TenantId = @TenantId
      AND i.Id IN (SELECT CAST(j.[value] AS INT) FROM OPENJSON(@ItemIdsJson) j)
    ORDER BY i.Id, g.Code;
END
GO
-- #endregion

-- #region usp_Drug_GetInteractions : RX-02 interactions among a set of ingredient codes
CREATE OR ALTER PROCEDURE dbo.usp_Drug_GetInteractions
    @IngredientCodesJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Codes TABLE (Code NVARCHAR(50) PRIMARY KEY);
    INSERT INTO @Codes (Code) SELECT DISTINCT CAST(j.[value] AS NVARCHAR(50)) FROM OPENJSON(@IngredientCodesJson) j;

    SELECT x.IngredientA, x.IngredientB, x.Severity, x.Description, x.Recommendation
    FROM dbo.DrugInteractions x
    WHERE x.IngredientA IN (SELECT Code FROM @Codes)
      AND x.IngredientB IN (SELECT Code FROM @Codes);
END
GO
-- #endregion

-- #region usp_Patient_Insert : a patient with their allergies; returns the Id
/* @AllergiesJson: [{ "AllergenCode", "Note" }] */
CREATE OR ALTER PROCEDURE dbo.usp_Patient_Insert
    @TenantId INT, @FullName NVARCHAR(150), @NationalId NVARCHAR(14) = NULL, @Mobile NVARCHAR(20) = NULL,
    @BirthDate DATE = NULL, @AllergiesJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.Patients (TenantId, FullName, NationalId, Mobile, BirthDate)
    VALUES (@TenantId, @FullName, @NationalId, @Mobile, @BirthDate);

    DECLARE @PatientId INT = CAST(SCOPE_IDENTITY() AS INT);

    INSERT INTO dbo.PatientAllergies (PatientId, TenantId, AllergenCode, Note)
    SELECT @PatientId, @TenantId, j.AllergenCode, j.Note
    FROM OPENJSON(@AllergiesJson) WITH (AllergenCode NVARCHAR(50), Note NVARCHAR(200)) j;

    SELECT @PatientId;
END
GO
-- #endregion

-- #region usp_Patient_NationalIdExists : 1 when the tenant already has a patient with this national ID
CREATE OR ALTER PROCEDURE dbo.usp_Patient_NationalIdExists
    @TenantId INT, @NationalId NVARCHAR(14)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.Patients WHERE TenantId = @TenantId AND NationalId = @NationalId)
                     THEN 1 ELSE 0 END AS BIT);
END
GO
-- #endregion

-- #region usp_Patient_GetById : result sets 1 the patient, 2 the allergies
CREATE OR ALTER PROCEDURE dbo.usp_Patient_GetById
    @TenantId INT, @PatientId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, TenantId, FullName, NationalId, Mobile, BirthDate
    FROM dbo.Patients
    WHERE Id = @PatientId AND TenantId = @TenantId;

    SELECT AllergenCode, Note
    FROM dbo.PatientAllergies
    WHERE PatientId = @PatientId AND TenantId = @TenantId
    ORDER BY AllergenCode;
END
GO
-- #endregion

-- #region usp_PatientAllergy_Insert : US-RX-3 records one allergy
CREATE OR ALTER PROCEDURE dbo.usp_PatientAllergy_Insert
    @TenantId INT, @PatientId INT, @AllergenCode NVARCHAR(50), @Note NVARCHAR(200) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.PatientAllergies (PatientId, TenantId, AllergenCode, Note)
    SELECT p.Id, @TenantId, @AllergenCode, @Note
    FROM dbo.Patients p
    WHERE p.Id = @PatientId AND p.TenantId = @TenantId
      AND NOT EXISTS (SELECT 1 FROM dbo.PatientAllergies a WHERE a.PatientId = p.Id AND a.AllergenCode = @AllergenCode);
END
GO
-- #endregion

-- #region usp_Patient_Search : by name, mobile or national ID
CREATE OR ALTER PROCEDURE dbo.usp_Patient_Search
    @TenantId INT, @Term NVARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (50) Id, FullName, NationalId, Mobile, BirthDate
    FROM dbo.Patients
    WHERE TenantId = @TenantId
      AND (FullName LIKE N'%' + @Term + N'%' OR Mobile LIKE @Term + N'%' OR NationalId = @Term)
    ORDER BY FullName;
END
GO
-- #endregion

-- #region usp_Patient_GetCurrentMedicationItemIds : RX-02 items dispensed recently, any branch
CREATE OR ALTER PROCEDURE dbo.usp_Patient_GetCurrentMedicationItemIds
    @TenantId INT, @PatientId INT, @SinceUtc DATETIME2(0), @ExceptPrescriptionId INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT DISTINCT dl.ItemId
    FROM dbo.Dispenses d
    JOIN dbo.DispenseLines dl ON dl.DispenseId = d.Id
    WHERE d.TenantId = @TenantId AND d.PatientId = @PatientId AND d.DispensedAtUtc >= @SinceUtc
      AND (@ExceptPrescriptionId IS NULL OR d.PrescriptionId IS NULL OR d.PrescriptionId <> @ExceptPrescriptionId);
END
GO
-- #endregion

-- #region usp_Patient_GetMedicationHistory : RX-08 everything dispensed to the patient, every branch, newest first
CREATE OR ALTER PROCEDURE dbo.usp_Patient_GetMedicationHistory
    @TenantId INT, @PatientId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (500)
           d.DispensedAtUtc, b.NameEn AS BranchName, dl.ItemId,
           CASE WHEN i.NameEn = N'' THEN i.NameAr ELSE i.NameEn END AS ItemName,
           dl.BaseUnits, d.PrescriptionId, pr.FullName AS PrescriberName, u.FullName AS PharmacistName, d.CounsellingNote
    FROM dbo.Dispenses d
    JOIN dbo.DispenseLines dl ON dl.DispenseId = d.Id
    JOIN dbo.Items i ON i.Id = dl.ItemId
    JOIN dbo.Branches b ON b.Id = d.BranchId
    JOIN dbo.Users u ON u.Id = d.PharmacistId
    LEFT JOIN dbo.Prescriptions p ON p.Id = d.PrescriptionId
    LEFT JOIN dbo.Prescribers pr ON pr.Id = p.PrescriberId
    WHERE d.TenantId = @TenantId AND d.PatientId = @PatientId
    ORDER BY d.DispensedAtUtc DESC, dl.SaleLineNumber;
END
GO
-- #endregion

-- #region usp_Prescriber_Insert : returns the Id
CREATE OR ALTER PROCEDURE dbo.usp_Prescriber_Insert
    @TenantId INT, @FullName NVARCHAR(150), @Specialty NVARCHAR(100) = NULL, @LicenceNumber NVARCHAR(50) = NULL,
    @Clinic NVARCHAR(150) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.Prescribers (TenantId, FullName, Specialty, LicenceNumber, Clinic)
    VALUES (@TenantId, @FullName, @Specialty, @LicenceNumber, @Clinic);

    SELECT CAST(SCOPE_IDENTITY() AS INT);
END
GO
-- #endregion

-- #region usp_Prescriber_GetById : one prescriber of the tenant, or no row
CREATE OR ALTER PROCEDURE dbo.usp_Prescriber_GetById
    @TenantId INT, @PrescriberId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, TenantId, FullName, Specialty, LicenceNumber, Clinic
    FROM dbo.Prescribers
    WHERE Id = @PrescriberId AND TenantId = @TenantId;
END
GO
-- #endregion

-- #region usp_Prescriber_Search : RX-09 directory, the most frequent prescribers first
CREATE OR ALTER PROCEDURE dbo.usp_Prescriber_Search
    @TenantId INT, @Term NVARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (50) pr.Id, pr.FullName, pr.Specialty, pr.LicenceNumber, pr.Clinic,
           (SELECT COUNT(*) FROM dbo.Prescriptions p WHERE p.TenantId = @TenantId AND p.PrescriberId = pr.Id) AS PrescriptionCount
    FROM dbo.Prescribers pr
    WHERE pr.TenantId = @TenantId
      AND (@Term = N'' OR pr.FullName LIKE N'%' + @Term + N'%' OR pr.LicenceNumber = @Term OR pr.Clinic LIKE N'%' + @Term + N'%')
    ORDER BY PrescriptionCount DESC, pr.FullName;
END
GO
-- #endregion

-- #region usp_Prescription_Insert : a prescription with its lines; returns the Id
/* @LinesJson: [{ "LineNumber", "ItemId", "ItemName", "Unit", "Quantity", "RefillsAllowed", "BaseUnitsPerUnit",
                  "DoseUnits", "FrequencyPerDay", "DurationDays", "Instructions" }] */
CREATE OR ALTER PROCEDURE dbo.usp_Prescription_Insert
    @TenantId INT, @BranchId INT, @PatientId INT, @PrescriberId INT, @PrescriptionNumber NVARCHAR(50) = NULL,
    @PrescribedOn DATE, @Diagnosis NVARCHAR(300) = NULL, @Status TINYINT, @RecordedBy INT, @RecordedAtUtc DATETIME2(0),
    @LinesJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.Prescriptions (TenantId, BranchId, PatientId, PrescriberId, PrescriptionNumber, PrescribedOn,
                                   Diagnosis, Status, RecordedBy, RecordedAtUtc)
    VALUES (@TenantId, @BranchId, @PatientId, @PrescriberId, @PrescriptionNumber, @PrescribedOn,
            @Diagnosis, @Status, @RecordedBy, @RecordedAtUtc);

    DECLARE @PrescriptionId INT = CAST(SCOPE_IDENTITY() AS INT);

    INSERT INTO dbo.PrescriptionLines (PrescriptionId, LineNumber, TenantId, ItemId, ItemName, Unit, Quantity,
                                       RefillsAllowed, BaseUnitsPerUnit, DoseUnits, FrequencyPerDay, DurationDays, Instructions)
    SELECT @PrescriptionId, j.LineNumber, @TenantId, j.ItemId, j.ItemName, j.Unit, j.Quantity, j.RefillsAllowed,
           j.BaseUnitsPerUnit, j.DoseUnits, j.FrequencyPerDay, j.DurationDays, j.Instructions
    FROM OPENJSON(@LinesJson)
         WITH (LineNumber INT, ItemId INT, ItemName NVARCHAR(200), Unit TINYINT, Quantity INT, RefillsAllowed INT,
               BaseUnitsPerUnit INT, DoseUnits DECIMAL(9,3), FrequencyPerDay INT, DurationDays INT,
               Instructions NVARCHAR(300)) j;

    SELECT @PrescriptionId;
END
GO
-- #endregion

-- #region usp_Prescription_GetById : result sets 1 the prescription, 2 its lines, 3 its images
/* UPDLOCK: inside a sale's transaction the prescription stays locked until the sale commits, so two
   tills cannot dispense the same remaining quantity (RX-06). Outside a transaction it costs nothing. */
CREATE OR ALTER PROCEDURE dbo.usp_Prescription_GetById
    @TenantId INT, @PrescriptionId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, TenantId, BranchId, PatientId, PrescriberId, PrescriptionNumber, PrescribedOn, Diagnosis, Status,
           RecordedBy, RecordedAtUtc
    FROM dbo.Prescriptions WITH (UPDLOCK, ROWLOCK)
    WHERE Id = @PrescriptionId AND TenantId = @TenantId;

    SELECT LineNumber, ItemId, ItemName, Unit, Quantity, RefillsAllowed, BaseUnitsPerUnit, DoseUnits, FrequencyPerDay,
           DurationDays, Instructions, DispensedBaseUnits
    FROM dbo.PrescriptionLines
    WHERE PrescriptionId = @PrescriptionId AND TenantId = @TenantId
    ORDER BY LineNumber;

    SELECT Id, ContentType, SizeBytes, UploadedAtUtc
    FROM dbo.PrescriptionImages
    WHERE PrescriptionId = @PrescriptionId AND TenantId = @TenantId
    ORDER BY Id;
END
GO
-- #endregion

-- #region usp_Prescription_Update : status and the dispensed quantity of every line (RX-06)
/* @LinesJson: [{ "LineNumber", "DispensedBaseUnits" }]. CK_PrescriptionLines_Dispensed is the final guard. */
CREATE OR ALTER PROCEDURE dbo.usp_Prescription_Update
    @TenantId INT, @PrescriptionId INT, @Status TINYINT, @LinesJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.Prescriptions SET Status = @Status WHERE Id = @PrescriptionId AND TenantId = @TenantId;

    UPDATE l SET DispensedBaseUnits = j.DispensedBaseUnits
    FROM dbo.PrescriptionLines l
    JOIN OPENJSON(@LinesJson) WITH (LineNumber INT, DispensedBaseUnits INT) j ON j.LineNumber = l.LineNumber
    WHERE l.PrescriptionId = @PrescriptionId AND l.TenantId = @TenantId;
END
GO
-- #endregion

-- #region usp_PrescriptionImage_Insert : RX-05 the index row of an encrypted image; returns the Id
CREATE OR ALTER PROCEDURE dbo.usp_PrescriptionImage_Insert
    @TenantId INT, @PrescriptionId INT, @StorageKey NVARCHAR(200), @ContentType NVARCHAR(50), @SizeBytes INT,
    @UploadedBy INT, @UploadedAtUtc DATETIME2(0)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.PrescriptionImages (TenantId, PrescriptionId, StorageKey, ContentType, SizeBytes, UploadedBy, UploadedAtUtc)
    SELECT @TenantId, p.Id, @StorageKey, @ContentType, @SizeBytes, @UploadedBy, @UploadedAtUtc
    FROM dbo.Prescriptions p
    WHERE p.Id = @PrescriptionId AND p.TenantId = @TenantId;

    SELECT CAST(SCOPE_IDENTITY() AS INT);
END
GO
-- #endregion

-- #region usp_PrescriptionImage_GetById : where an image of the tenant is stored, or no row
CREATE OR ALTER PROCEDURE dbo.usp_PrescriptionImage_GetById
    @TenantId INT, @PrescriptionId INT, @ImageId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, PrescriptionId, StorageKey, ContentType
    FROM dbo.PrescriptionImages
    WHERE Id = @ImageId AND PrescriptionId = @PrescriptionId AND TenantId = @TenantId;
END
GO
-- #endregion

-- #region usp_Prescription_GetOpen : the "refills due" list, optionally for one patient
CREATE OR ALTER PROCEDURE dbo.usp_Prescription_GetOpen
    @TenantId INT, @PatientId INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (200)
           p.Id, p.PatientId, pa.FullName AS PatientName, pa.Mobile AS PatientMobile, pr.FullName AS PrescriberName,
           p.PrescribedOn, p.Status,
           ISNULL((SELECT STRING_AGG(l.ItemName + N' x ' + FORMAT(
                              CAST(l.Quantity * (1 + l.RefillsAllowed) * l.BaseUnitsPerUnit - l.DispensedBaseUnits AS DECIMAL(18,3))
                              / l.BaseUnitsPerUnit, N'0.##'), N', ')
                   FROM dbo.PrescriptionLines l
                   WHERE l.PrescriptionId = p.Id
                     AND l.DispensedBaseUnits < l.Quantity * (1 + l.RefillsAllowed) * l.BaseUnitsPerUnit), N'') AS Remaining
    FROM dbo.Prescriptions p
    JOIN dbo.Patients pa ON pa.Id = p.PatientId
    JOIN dbo.Prescribers pr ON pr.Id = p.PrescriberId
    WHERE p.TenantId = @TenantId AND p.Status IN (1, 2)
      AND (@PatientId IS NULL OR p.PatientId = @PatientId)
    ORDER BY p.PrescribedOn;
END
GO
-- #endregion

-- #region usp_Dispense_Insert : the dispense record with its lines and alerts; returns the Id
/* @LinesJson:  [{ "SaleLineNumber", "PrescriptionLineNumber", "ItemId", "BaseUnits" }]
   @AlertsJson: [{ "Code", "Type", "Severity", "Message", "OverrideReason" }] */
CREATE OR ALTER PROCEDURE dbo.usp_Dispense_Insert
    @TenantId INT, @BranchId INT, @PrescriptionId INT = NULL, @PatientId INT = NULL, @SaleId BIGINT, @PharmacistId INT,
    @CounsellingNote NVARCHAR(1000) = NULL, @PrescriptionPending BIT, @DispensedAtUtc DATETIME2(0),
    @LinesJson NVARCHAR(MAX), @AlertsJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.Dispenses (TenantId, BranchId, PrescriptionId, PatientId, SaleId, PharmacistId, CounsellingNote,
                               PrescriptionPending, DispensedAtUtc)
    VALUES (@TenantId, @BranchId, @PrescriptionId, @PatientId, @SaleId, @PharmacistId, @CounsellingNote,
            @PrescriptionPending, @DispensedAtUtc);

    DECLARE @DispenseId BIGINT = CAST(SCOPE_IDENTITY() AS BIGINT);

    INSERT INTO dbo.DispenseLines (DispenseId, SaleLineNumber, TenantId, PrescriptionLineNumber, ItemId, BaseUnits)
    SELECT @DispenseId, j.SaleLineNumber, @TenantId, j.PrescriptionLineNumber, j.ItemId, j.BaseUnits
    FROM OPENJSON(@LinesJson) WITH (SaleLineNumber INT, PrescriptionLineNumber INT, ItemId INT, BaseUnits INT) j;

    INSERT INTO dbo.DispenseAlerts (DispenseId, TenantId, Code, Type, Severity, Message, OverrideReason)
    SELECT @DispenseId, @TenantId, j.Code, j.Type, j.Severity, j.Message, j.OverrideReason
    FROM OPENJSON(@AlertsJson)
         WITH (Code NVARCHAR(150), Type TINYINT, Severity TINYINT, Message NVARCHAR(600), OverrideReason NVARCHAR(500)) j;

    SELECT @DispenseId;
END
GO
-- #endregion

-- #region usp_Dispense_GetLabelRows : RX-07 what a sale's dispensing labels need
CREATE OR ALTER PROCEDURE dbo.usp_Dispense_GetLabelRows
    @TenantId INT, @SaleId BIGINT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT ISNULL(pa.FullName, N'') AS PatientName, i.NameEn AS ItemNameEn, i.NameAr AS ItemNameAr, i.DosageForm,
           dl.BaseUnits, pl.DoseUnits, pl.FrequencyPerDay, pl.DurationDays, pl.Instructions,
           dr.LabelWarningEn AS WarningEn, dr.LabelWarningAr AS WarningAr,
           t.LegalNameAr AS PharmacyName, b.NameAr AS BranchName, d.DispensedAtUtc
    FROM dbo.Dispenses d
    JOIN dbo.DispenseLines dl ON dl.DispenseId = d.Id
    JOIN dbo.Items i ON i.Id = dl.ItemId
    JOIN dbo.Branches b ON b.Id = d.BranchId
    JOIN dbo.Tenants t ON t.Id = d.TenantId
    LEFT JOIN dbo.Patients pa ON pa.Id = d.PatientId
    LEFT JOIN dbo.PrescriptionLines pl ON pl.PrescriptionId = d.PrescriptionId AND pl.LineNumber = dl.PrescriptionLineNumber
    LEFT JOIN dbo.Drugs dr ON dr.Id = i.DrugId
    WHERE d.TenantId = @TenantId AND d.SaleId = @SaleId
    ORDER BY dl.SaleLineNumber;
END
GO
-- #endregion
