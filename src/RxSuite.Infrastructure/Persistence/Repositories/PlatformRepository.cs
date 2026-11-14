using RxSuite.Application.Abstractions;
using RxSuite.Application.Features.Platform;
using RxSuite.Domain.Platform;
using RxSuite.Infrastructure.Persistence.Connection;

namespace RxSuite.Infrastructure.Persistence.Repositories;

/// <summary>
/// REPOSITORY for plans, tenants, subscriptions and branches. These calls take the tenant
/// explicitly because sign-up, the payment webhook and the nightly job run with no logged-in user.
/// </summary>
internal sealed class PlatformRepository(IDbSession session, ICurrentUser currentUser)
    : StoredProcedureRepository(session, currentUser), IPlatformRepository
{
    #region Plans

    public Task<IReadOnlyList<Plan>> GetPlansAsync(CancellationToken ct) =>
        QueryAsync<Plan>("dbo.usp_Plan_GetAll", null, ct);

    #endregion

    #region Tenants

    public async Task<int> AddTenantAsync(Tenant tenant, CancellationToken ct)
    {
        var id = await ExecuteScalarAsync<int>("dbo.usp_Tenant_Insert", new
        {
            tenant.LegalNameEn, tenant.LegalNameAr, CustomerType = (byte)tenant.CustomerType,
            tenant.TaxRegistrationNumber, tenant.CommercialRegisterNumber, tenant.LicenceNumber,
            tenant.Governorate, tenant.City, tenant.Address, Status = (byte)tenant.Status
        }, ct);

        tenant.AssignId(id);
        return id;
    }

    public async Task<TenantStatus?> GetTenantStatusAsync(int tenantId, CancellationToken ct) =>
        (TenantStatus?)await QuerySingleOrDefaultAsync<byte?>("dbo.usp_Tenant_GetStatus", new { TenantId = tenantId }, ct);

    public Task SetTenantStatusAsync(int tenantId, TenantStatus status, CancellationToken ct) =>
        ExecuteAsync("dbo.usp_Tenant_SetStatus", new { TenantId = tenantId, Status = (byte)status }, ct);

    #endregion

    #region Subscriptions

    public async Task<int> AddSubscriptionAsync(Subscription s, CancellationToken ct)
    {
        var id = await ExecuteScalarAsync<int>("dbo.usp_Subscription_Insert", new
        {
            s.TenantId, s.PlanId, BillingCycle = (byte)s.BillingCycle, s.BranchCount, s.IsTrial, s.StartDate,
            s.RenewalDate, s.Price, s.AutoRenew
        }, ct);

        s.AssignId(id);
        return id;
    }

    public Task<Subscription?> GetSubscriptionAsync(int tenantId, CancellationToken ct) =>
        QuerySingleOrDefaultAsync<Subscription>("dbo.usp_Subscription_GetByTenant", new { TenantId = tenantId }, ct);

    public Task UpdateSubscriptionAsync(Subscription s, CancellationToken ct) =>
        ExecuteAsync("dbo.usp_Subscription_Update", new
        {
            s.Id, s.TenantId, s.PlanId, BillingCycle = (byte)s.BillingCycle, s.BranchCount, s.IsTrial,
            s.RenewalDate, s.Price
        }, ct);

    public Task AddSubscriptionPaymentAsync(int tenantId, decimal amount, string method, string gatewayReference, CancellationToken ct) =>
        ExecuteAsync("dbo.usp_SubscriptionPayment_Insert",
            new { TenantId = tenantId, Amount = amount, Method = method, GatewayReference = gatewayReference }, ct);

    public Task<IReadOnlyList<SubscriptionState>> GetSubscriptionStatesAsync(CancellationToken ct) =>
        QueryAsync<SubscriptionState>("dbo.usp_Job_Subscription_GetStates", null, ct);

    #endregion

    #region Branches

    public async Task<int> AddBranchAsync(Branch branch, CancellationToken ct)
    {
        var id = await ExecuteScalarAsync<int>("dbo.usp_Branch_Insert",
            new { branch.TenantId, branch.NameEn, branch.NameAr, Type = (byte)branch.Type, branch.Address }, ct);

        branch.AssignId(id);
        return id;
    }

    public Task<IReadOnlyList<Branch>> GetBranchesAsync(int tenantId, CancellationToken ct) =>
        QueryAsync<Branch>("dbo.usp_Branch_GetByTenant", new { TenantId = tenantId }, ct);

    #endregion
}
