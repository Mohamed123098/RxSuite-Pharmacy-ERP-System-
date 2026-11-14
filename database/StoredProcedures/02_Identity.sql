/* Identity and audit procedures (BRD 8.2). */

-- #region usp_User_Insert : creates a user with its branches (JSON array of branch Ids) and returns its Id
CREATE OR ALTER PROCEDURE dbo.usp_User_Insert
    @TenantId INT, @FullName NVARCHAR(150), @Mobile NVARCHAR(20), @Email NVARCHAR(150),
    @PasswordHash NVARCHAR(200), @PinHash NVARCHAR(200) = NULL, @RoleId INT, @IsPharmacist BIT,
    @LicenceNumber NVARCHAR(30) = NULL, @LicenceExpiry DATE = NULL, @BranchIdsJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.Users (TenantId, FullName, Mobile, Email, PasswordHash, PinHash, RoleId, IsPharmacist, LicenceNumber, LicenceExpiry)
    VALUES (@TenantId, @FullName, @Mobile, @Email, @PasswordHash, @PinHash, @RoleId, @IsPharmacist, @LicenceNumber, @LicenceExpiry);

    DECLARE @UserId INT = CAST(SCOPE_IDENTITY() AS INT);

    -- USR-03: only branches of the same tenant can be assigned.
    INSERT INTO dbo.UserBranches (UserId, BranchId, TenantId)
    SELECT @UserId, b.Id, @TenantId
    FROM OPENJSON(@BranchIdsJson) j
    JOIN dbo.Branches b ON b.Id = CAST(j.[value] AS INT) AND b.TenantId = @TenantId;

    SELECT @UserId;
END
GO
-- #endregion

-- #region usp_User_GetByLogin : login lookup by mobile or e-mail (the only cross-tenant user read)
CREATE OR ALTER PROCEDURE dbo.usp_User_GetByLogin
    @Login NVARCHAR(150)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, TenantId, FullName, Mobile, Email, PasswordHash, PinHash, RoleId, IsPharmacist, LicenceNumber,
           LicenceExpiry, IsActive, FailedLoginCount, LockedUntilUtc, LastLoginUtc
    FROM dbo.Users
    WHERE @Login <> N'' AND (Email = @Login OR Mobile = @Login);
END
GO
-- #endregion

-- #region usp_User_LoginExists : 1 when the mobile or e-mail is already registered
CREATE OR ALTER PROCEDURE dbo.usp_User_LoginExists
    @Mobile NVARCHAR(20), @Email NVARCHAR(150)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.Users
                                  WHERE (@Email <> N'' AND Email = @Email) OR (@Mobile <> N'' AND Mobile = @Mobile))
                     THEN 1 ELSE 0 END AS BIT);
END
GO
-- #endregion

-- #region usp_User_GetById : one user of the tenant, or no row
CREATE OR ALTER PROCEDURE dbo.usp_User_GetById
    @TenantId INT, @UserId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, TenantId, FullName, Mobile, Email, PasswordHash, PinHash, RoleId, IsPharmacist, LicenceNumber,
           LicenceExpiry, IsActive, FailedLoginCount, LockedUntilUtc, LastLoginUtc
    FROM dbo.Users
    WHERE Id = @UserId AND TenantId = @TenantId;
END
GO
-- #endregion

-- #region usp_User_SaveLoginState : failed-attempt counter, lockout and last login (USR-04)
CREATE OR ALTER PROCEDURE dbo.usp_User_SaveLoginState
    @TenantId INT, @UserId INT, @FailedLoginCount INT, @LockedUntilUtc DATETIME2(0) = NULL, @LastLoginUtc DATETIME2(0) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.Users
    SET FailedLoginCount = @FailedLoginCount, LockedUntilUtc = @LockedUntilUtc, LastLoginUtc = @LastLoginUtc
    WHERE Id = @UserId AND TenantId = @TenantId;
END
GO
-- #endregion

-- #region usp_User_Disable : blocks a user; the row is kept for history (BR-USR-3)
CREATE OR ALTER PROCEDURE dbo.usp_User_Disable
    @TenantId INT, @UserId INT
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.Users SET IsActive = 0 WHERE Id = @UserId AND TenantId = @TenantId;
END
GO
-- #endregion

-- #region usp_User_CountActive : active users of the tenant, for the plan limit (SUB-12)
CREATE OR ALTER PROCEDURE dbo.usp_User_CountActive
    @TenantId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT COUNT(*) FROM dbo.Users WHERE TenantId = @TenantId AND IsActive = 1;
END
GO
-- #endregion

-- #region usp_User_GetBranchIds : the branches a user is assigned to (USR-03)
CREATE OR ALTER PROCEDURE dbo.usp_User_GetBranchIds
    @TenantId INT, @UserId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT BranchId FROM dbo.UserBranches WHERE UserId = @UserId AND TenantId = @TenantId;
END
GO
-- #endregion

-- #region usp_User_GetAll : the users list screen
CREATE OR ALTER PROCEDURE dbo.usp_User_GetAll
    @TenantId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT u.Id, u.FullName, u.Mobile, u.Email, r.Code AS RoleCode, u.IsPharmacist, u.LicenceExpiry, u.IsActive, u.LastLoginUtc
    FROM dbo.Users u
    JOIN dbo.Roles r ON r.Id = u.RoleId
    WHERE u.TenantId = @TenantId
    ORDER BY u.FullName;
END
GO
-- #endregion

-- #region usp_Role_Get : a role by Id or by code. Result set 1 = the role, result set 2 = its permissions
CREATE OR ALTER PROCEDURE dbo.usp_Role_Get
    @RoleId INT = NULL, @Code NVARCHAR(30) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Id INT = (SELECT Id FROM dbo.Roles WHERE (@RoleId IS NOT NULL AND Id = @RoleId) OR (@Code IS NOT NULL AND Code = @Code));

    SELECT Id, Code, NameEn, NameAr, MaxDiscountPercent, HasAllBranches FROM dbo.Roles WHERE Id = @Id;
    SELECT Permission FROM dbo.RolePermissions WHERE RoleId = @Id;
END
GO
-- #endregion

-- #region usp_Audit_Insert : adds one audit entry. There is deliberately no update or delete procedure (USR-12)
CREATE OR ALTER PROCEDURE dbo.usp_Audit_Insert
    @TenantId INT, @TimestampUtc DATETIME2(0), @UserId INT = NULL, @BranchId INT = NULL,
    @Module NVARCHAR(30), @Action NVARCHAR(50), @RecordId NVARCHAR(50) = NULL,
    @OldValue NVARCHAR(MAX) = NULL, @NewValue NVARCHAR(MAX) = NULL, @Reason NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.AuditLog (TenantId, TimestampUtc, UserId, BranchId, Module, Action, RecordId, OldValue, NewValue, Reason)
    VALUES (@TenantId, @TimestampUtc, @UserId, @BranchId, @Module, @Action, @RecordId, @OldValue, @NewValue, @Reason);
END
GO
-- #endregion

-- #region usp_Audit_GetPaged : audit viewer. Result set 1 = one page (newest first), result set 2 = total count
CREATE OR ALTER PROCEDURE dbo.usp_Audit_GetPaged
    @TenantId INT, @PageNumber INT, @PageSize INT, @UserId INT = NULL, @BranchId INT = NULL,
    @Module NVARCHAR(30) = NULL, @FromUtc DATETIME2(0) = NULL, @ToUtc DATETIME2(0) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT a.Id, a.TimestampUtc, a.UserId, u.FullName AS UserName, a.BranchId, a.Module, a.Action, a.RecordId,
           a.OldValue, a.NewValue, a.Reason
    FROM dbo.AuditLog a
    LEFT JOIN dbo.Users u ON u.Id = a.UserId
    WHERE a.TenantId = @TenantId
      AND (@UserId IS NULL OR a.UserId = @UserId)
      AND (@BranchId IS NULL OR a.BranchId = @BranchId)
      AND (@Module IS NULL OR a.Module = @Module)
      AND (@FromUtc IS NULL OR a.TimestampUtc >= @FromUtc)
      AND (@ToUtc IS NULL OR a.TimestampUtc <= @ToUtc)
    ORDER BY a.TimestampUtc DESC, a.Id DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY
    OPTION (RECOMPILE);   -- optional filters: plan for the actual parameter values

    SELECT COUNT(*)
    FROM dbo.AuditLog a
    WHERE a.TenantId = @TenantId
      AND (@UserId IS NULL OR a.UserId = @UserId)
      AND (@BranchId IS NULL OR a.BranchId = @BranchId)
      AND (@Module IS NULL OR a.Module = @Module)
      AND (@FromUtc IS NULL OR a.TimestampUtc >= @FromUtc)
      AND (@ToUtc IS NULL OR a.TimestampUtc <= @ToUtc)
    OPTION (RECOMPILE);
END
GO
-- #endregion
