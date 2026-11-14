using Dapper;
using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Application.Features.Identity;
using RxSuite.Domain.Identity;
using RxSuite.Infrastructure.Persistence.Connection;

namespace RxSuite.Infrastructure.Persistence.Repositories;

#region Users and roles

/// <summary>REPOSITORY for users and roles.</summary>
internal sealed class UserRepository(IDbSession session, ICurrentUser currentUser)
    : StoredProcedureRepository(session, currentUser), IUserRepository
{
    #region Commands

    public async Task<int> AddAsync(User user, IEnumerable<int> branchIds, CancellationToken ct)
    {
        var id = await ExecuteScalarAsync<int>("dbo.usp_User_Insert", new
        {
            user.TenantId, user.FullName, user.Mobile, user.Email, user.PasswordHash, user.PinHash, user.RoleId,
            user.IsPharmacist, user.LicenceNumber, user.LicenceExpiry, BranchIdsJson = Json(branchIds)
        }, ct);

        user.AssignId(id);
        return id;
    }

    public Task SaveLoginStateAsync(User user, CancellationToken ct) =>
        ExecuteAsync("dbo.usp_User_SaveLoginState", new
        {
            user.TenantId, UserId = user.Id, user.FailedLoginCount, user.LockedUntilUtc, user.LastLoginUtc
        }, ct);

    public Task DisableAsync(int userId, CancellationToken ct) =>
        ExecuteAsync("dbo.usp_User_Disable", Scoped(new { UserId = userId }), ct);

    #endregion

    #region Queries

    public Task<User?> GetByLoginAsync(string login, CancellationToken ct) =>
        QuerySingleOrDefaultAsync<User>("dbo.usp_User_GetByLogin", new { Login = login }, ct);

    public Task<bool> LoginExistsAsync(string mobile, string email, CancellationToken ct) =>
        ExecuteScalarAsync<bool>("dbo.usp_User_LoginExists", new { Mobile = mobile, Email = email }, ct);

    public Task<User?> GetByIdAsync(int userId, CancellationToken ct) =>
        QuerySingleOrDefaultAsync<User>("dbo.usp_User_GetById", Scoped(new { UserId = userId }), ct);

    public Task<int> CountActiveAsync(CancellationToken ct) =>
        ExecuteScalarAsync<int>("dbo.usp_User_CountActive", Scoped(), ct);

    public Task<IReadOnlyList<int>> GetBranchIdsAsync(int tenantId, int userId, CancellationToken ct) =>
        QueryAsync<int>("dbo.usp_User_GetBranchIds", new { TenantId = tenantId, UserId = userId }, ct);

    public Task<IReadOnlyList<UserDto>> GetAllAsync(CancellationToken ct) =>
        QueryAsync<UserDto>("dbo.usp_User_GetAll", Scoped(), ct);

    public Task<Role?> GetRoleAsync(int roleId, CancellationToken ct) =>
        QueryMultipleAsync("dbo.usp_Role_Get", new { RoleId = roleId, Code = (string?)null }, ReadRoleAsync, ct);

    public Task<Role?> GetRoleByCodeAsync(string code, CancellationToken ct) =>
        QueryMultipleAsync("dbo.usp_Role_Get", new { RoleId = (int?)null, Code = code }, ReadRoleAsync, ct);

    /// <summary>Result set 1: the role. Result set 2: its permission codes.</summary>
    private static async Task<Role?> ReadRoleAsync(SqlMapper.GridReader grid)
    {
        var role = await grid.ReadSingleOrDefaultAsync<Role>();
        var permissions = await grid.ReadAsync<string>();

        role?.LoadPermissions(permissions);
        return role;
    }

    #endregion
}

#endregion

#region Audit trail

/// <summary>REPOSITORY for the audit trail. Insert and read only: there is no update or delete (USR-12).</summary>
internal sealed class AuditRepository(IDbSession session, ICurrentUser currentUser)
    : StoredProcedureRepository(session, currentUser), IAuditRepository
{
    public Task AddAsync(AuditEntry e, CancellationToken ct) =>
        ExecuteAsync("dbo.usp_Audit_Insert", new
        {
            e.TenantId, e.TimestampUtc, e.UserId, e.BranchId, e.Module, e.Action, e.RecordId, e.OldValue,
            e.NewValue, e.Reason
        }, ct);

    public Task<PagedResult<AuditEntryDto>> GetPagedAsync(GetAuditLogQuery f, CancellationToken ct) =>
        QueryMultipleAsync("dbo.usp_Audit_GetPaged",
            Scoped(new { f.PageNumber, f.PageSize, f.UserId, f.BranchId, f.Module, f.FromUtc, f.ToUtc }),
            async grid => new PagedResult<AuditEntryDto>(
                (await grid.ReadAsync<AuditEntryDto>()).ToList(), await grid.ReadSingleAsync<int>(), f.PageNumber, f.PageSize),
            ct);
}

#endregion
