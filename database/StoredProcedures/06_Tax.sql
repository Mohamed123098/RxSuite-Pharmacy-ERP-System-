/* ETA document procedures (BRD 8.9, Figure 15). */

-- #region usp_EtaDocument_Insert : queues a tax document for a sale or a return; returns its Id
CREATE OR ALTER PROCEDURE dbo.usp_EtaDocument_Insert
    @TenantId INT, @BranchId INT, @Type TINYINT, @SaleId BIGINT = NULL, @SaleReturnId BIGINT = NULL,
    @OriginalDocumentId BIGINT = NULL, @Status TINYINT, @CreatedAtUtc DATETIME2(0)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.EtaDocuments (TenantId, BranchId, Type, SaleId, SaleReturnId, OriginalDocumentId, Status, CreatedAtUtc)
    VALUES (@TenantId, @BranchId, @Type, @SaleId, @SaleReturnId, @OriginalDocumentId, @Status, @CreatedAtUtc);

    SELECT CAST(SCOPE_IDENTITY() AS BIGINT);
END
GO
-- #endregion

-- #region usp_EtaDocument_GetBySale : the tax document of a sale, or no row
CREATE OR ALTER PROCEDURE dbo.usp_EtaDocument_GetBySale
    @TenantId INT, @SaleId BIGINT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (1) Id, TenantId, BranchId, Type, SaleId, SaleReturnId, OriginalDocumentId, Status, Uuid, LongId,
           QrCode, Errors, Attempts, CreatedAtUtc, SubmittedAtUtc
    FROM dbo.EtaDocuments
    WHERE TenantId = @TenantId AND SaleId = @SaleId
    ORDER BY Id;
END
GO
-- #endregion

-- #region usp_EtaDocument_GetById : one tax document of the tenant, or no row
CREATE OR ALTER PROCEDURE dbo.usp_EtaDocument_GetById
    @TenantId INT, @DocumentId BIGINT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, TenantId, BranchId, Type, SaleId, SaleReturnId, OriginalDocumentId, Status, Uuid, LongId,
           QrCode, Errors, Attempts, CreatedAtUtc, SubmittedAtUtc
    FROM dbo.EtaDocuments
    WHERE Id = @DocumentId AND TenantId = @TenantId;
END
GO
-- #endregion

-- #region usp_EtaDocument_Update : saves the status and ETA's answer (UUID, QR or errors)
CREATE OR ALTER PROCEDURE dbo.usp_EtaDocument_Update
    @TenantId INT, @DocumentId BIGINT, @Status TINYINT, @Uuid NVARCHAR(100) = NULL, @LongId NVARCHAR(200) = NULL,
    @QrCode NVARCHAR(MAX) = NULL, @Errors NVARCHAR(MAX) = NULL, @Attempts INT, @SubmittedAtUtc DATETIME2(0) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.EtaDocuments
    SET Status = @Status, Uuid = @Uuid, LongId = @LongId, QrCode = @QrCode, Errors = @Errors,
        Attempts = @Attempts, SubmittedAtUtc = @SubmittedAtUtc
    WHERE Id = @DocumentId AND TenantId = @TenantId;
END
GO
-- #endregion

-- #region usp_Job_EtaDocument_GetQueued : background job - the oldest queued documents of ALL tenants (ACC-07)
CREATE OR ALTER PROCEDURE dbo.usp_Job_EtaDocument_GetQueued
    @Max INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Max) Id, TenantId, BranchId, Type, SaleId, SaleReturnId, OriginalDocumentId, Status, Uuid, LongId,
           QrCode, Errors, Attempts, CreatedAtUtc, SubmittedAtUtc
    FROM dbo.EtaDocuments
    WHERE Status = 2
    ORDER BY CreatedAtUtc, Id;
END
GO
-- #endregion

-- #region usp_EtaDocument_GetPaged : documents monitor. Result set 1 = one page (newest first), 2 = total count
CREATE OR ALTER PROCEDURE dbo.usp_EtaDocument_GetPaged
    @TenantId INT, @Status TINYINT = NULL, @PageNumber INT, @PageSize INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Id, BranchId, Type, SaleId, SaleReturnId, Status, Uuid, Errors, Attempts, CreatedAtUtc, SubmittedAtUtc
    FROM dbo.EtaDocuments
    WHERE TenantId = @TenantId AND (@Status IS NULL OR Status = @Status)
    ORDER BY CreatedAtUtc DESC, Id DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY
    OPTION (RECOMPILE);

    SELECT COUNT(*)
    FROM dbo.EtaDocuments
    WHERE TenantId = @TenantId AND (@Status IS NULL OR Status = @Status)
    OPTION (RECOMPILE);
END
GO
-- #endregion
