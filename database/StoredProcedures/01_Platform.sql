/* Platform procedures (BRD 8.1). These take @TenantId explicitly: sign-up, the payment webhook
   and the nightly job run before or without a logged-in user. */

-- #region usp_Plan_GetAll : every plan, for the pricing page and for price checks
CREATE OR ALTER PROCEDURE dbo.usp_Plan_GetAll
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, Code, NameEn, NameAr, MonthlyPricePerBranch, MaxBranches, MaxUsersPerBranch, IsActive
    FROM dbo.Plans
    ORDER BY Id;
END
GO
-- #endregion

-- #region usp_Tenant_Insert : creates a tenant and returns its Id
CREATE OR ALTER PROCEDURE dbo.usp_Tenant_Insert
    @LegalNameEn NVARCHAR(200), @LegalNameAr NVARCHAR(200), @CustomerType TINYINT,
    @TaxRegistrationNumber NVARCHAR(30), @CommercialRegisterNumber NVARCHAR(30), @LicenceNumber NVARCHAR(30),
    @Governorate NVARCHAR(60), @City NVARCHAR(60), @Address NVARCHAR(300), @Status TINYINT
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.Tenants (LegalNameEn, LegalNameAr, CustomerType, TaxRegistrationNumber, CommercialRegisterNumber,
                             LicenceNumber, Governorate, City, Address, Status)
    VALUES (@LegalNameEn, @LegalNameAr, @CustomerType, @TaxRegistrationNumber, @CommercialRegisterNumber,
            @LicenceNumber, @Governorate, @City, @Address, @Status);

    SELECT CAST(SCOPE_IDENTITY() AS INT);
END
GO
-- #endregion

-- #region usp_Tenant_GetStatus : the tenant's status, or no row
CREATE OR ALTER PROCEDURE dbo.usp_Tenant_GetStatus
    @TenantId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Status FROM dbo.Tenants WHERE Id = @TenantId;
END
GO
-- #endregion

-- #region usp_Tenant_SetStatus : moves the tenant to a new status (trial/active/grace/read-only)
CREATE OR ALTER PROCEDURE dbo.usp_Tenant_SetStatus
    @TenantId INT, @Status TINYINT
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.Tenants SET Status = @Status WHERE Id = @TenantId;
END
GO
-- #endregion

-- #region usp_Subscription_Insert : creates the tenant's subscription and returns its Id
CREATE OR ALTER PROCEDURE dbo.usp_Subscription_Insert
    @TenantId INT, @PlanId INT, @BillingCycle TINYINT, @BranchCount INT, @IsTrial BIT,
    @StartDate DATE, @RenewalDate DATE, @Price DECIMAL(18,2), @AutoRenew BIT
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.Subscriptions (TenantId, PlanId, BillingCycle, BranchCount, IsTrial, StartDate, RenewalDate, Price, AutoRenew)
    VALUES (@TenantId, @PlanId, @BillingCycle, @BranchCount, @IsTrial, @StartDate, @RenewalDate, @Price, @AutoRenew);

    SELECT CAST(SCOPE_IDENTITY() AS INT);
END
GO
-- #endregion

-- #region usp_Subscription_GetByTenant : the tenant's subscription, or no row
CREATE OR ALTER PROCEDURE dbo.usp_Subscription_GetByTenant
    @TenantId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, TenantId, PlanId, BillingCycle, BranchCount, IsTrial, StartDate, RenewalDate, Price, AutoRenew
    FROM dbo.Subscriptions
    WHERE TenantId = @TenantId;
END
GO
-- #endregion

-- #region usp_Subscription_Update : saves the plan, cycle, branches and renewal date after a payment
CREATE OR ALTER PROCEDURE dbo.usp_Subscription_Update
    @Id INT, @TenantId INT, @PlanId INT, @BillingCycle TINYINT, @BranchCount INT, @IsTrial BIT,
    @RenewalDate DATE, @Price DECIMAL(18,2)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.Subscriptions
    SET PlanId = @PlanId, BillingCycle = @BillingCycle, BranchCount = @BranchCount, IsTrial = @IsTrial,
        RenewalDate = @RenewalDate, Price = @Price
    WHERE Id = @Id AND TenantId = @TenantId;
END
GO
-- #endregion

-- #region usp_SubscriptionPayment_Insert : records a confirmed gateway payment (reference only, no card data)
CREATE OR ALTER PROCEDURE dbo.usp_SubscriptionPayment_Insert
    @TenantId INT, @Amount DECIMAL(18,2), @Method NVARCHAR(30), @GatewayReference NVARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.SubscriptionPayments (TenantId, Amount, Method, GatewayReference)
    VALUES (@TenantId, @Amount, @Method, @GatewayReference);
END
GO
-- #endregion

-- #region usp_Job_Subscription_GetStates : nightly job - renewal date and current status of every tenant
CREATE OR ALTER PROCEDURE dbo.usp_Job_Subscription_GetStates
AS
BEGIN
    SET NOCOUNT ON;
    SELECT s.TenantId, s.IsTrial, s.RenewalDate, t.Status
    FROM dbo.Subscriptions s
    JOIN dbo.Tenants t ON t.Id = s.TenantId
    WHERE t.Status <> 5;   -- cancelled tenants are left alone
END
GO
-- #endregion

-- #region usp_Branch_Insert : adds a branch or warehouse and returns its Id
CREATE OR ALTER PROCEDURE dbo.usp_Branch_Insert
    @TenantId INT, @NameEn NVARCHAR(100), @NameAr NVARCHAR(100), @Type TINYINT, @Address NVARCHAR(300)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.Branches (TenantId, NameEn, NameAr, Type, Address)
    VALUES (@TenantId, @NameEn, @NameAr, @Type, @Address);

    SELECT CAST(SCOPE_IDENTITY() AS INT);
END
GO
-- #endregion

-- #region usp_Branch_GetByTenant : the tenant's branches and warehouses
CREATE OR ALTER PROCEDURE dbo.usp_Branch_GetByTenant
    @TenantId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, TenantId, NameEn, NameAr, Type, Address, IsActive
    FROM dbo.Branches
    WHERE TenantId = @TenantId
    ORDER BY Id;
END
GO
-- #endregion
