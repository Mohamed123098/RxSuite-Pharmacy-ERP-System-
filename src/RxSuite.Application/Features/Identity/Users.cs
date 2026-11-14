using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Domain.Common;
using RxSuite.Domain.Identity;
using RxSuite.Domain.Rules;

namespace RxSuite.Application.Features.Identity;

// USE CASES: manage the tenant's users (USR-01, USR-03, USR-10; BR-USR-3; SUB-12 user limit).

#region DTO

public sealed class UserDto
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Mobile { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string RoleCode { get; set; } = string.Empty;
    public bool IsPharmacist { get; set; }
    public DateOnly? LicenceExpiry { get; set; }
    public bool IsActive { get; set; }
    public DateTime? LastLoginUtc { get; set; }
}

#endregion

#region Command: create a user

[RequiresPermission(Permissions.UsersManage)]
public sealed record CreateUserCommand(string FullName, string Mobile, string Email, string Password, string? Pin,
    string RoleCode, IReadOnlyList<int> BranchIds, bool IsPharmacist, string? LicenceNumber, DateOnly? LicenceExpiry)
    : ICommand<int>;

internal sealed class CreateUserValidator(BusinessRules rules) : IValidator<CreateUserCommand>
{
    public IReadOnlyList<string> Validate(CreateUserCommand c)
    {
        var problems = PasswordPolicy.Check(c.Password, rules.Security).ToList();

        // USR-06/07: a PIN is 4 to 6 digits.
        if (c.Pin is not null && (c.Pin.Length is < 4 or > 6 || !c.Pin.All(char.IsDigit)))
            problems.Add("The PIN must be 4 to 6 digits.");
        if (c.BranchIds is null || c.BranchIds.Count == 0) problems.Add("Assign the user to at least one branch.");

        return problems;
    }
}

internal sealed class CreateUserHandler(
    IUserRepository users, IPlatformRepository platform, ISecretHasher hasher, ICurrentUser currentUser,
    IAuditTrail audit, BusinessRules rules) : IRequestHandler<CreateUserCommand, int>
{
    public async Task<Result<int>> HandleAsync(CreateUserCommand c, CancellationToken ct)
    {
        // SUB-12: the plan limits the number of users.
        var subscription = await platform.GetSubscriptionAsync(currentUser.TenantId, ct);
        if (subscription is null) return Errors.SubscriptionNotFound;

        var plan = (await platform.GetPlansAsync(ct)).First(p => p.Id == subscription.PlanId);
        if (subscription.MaxUsers(plan, rules.Subscription) is { } limit && await users.CountActiveAsync(ct) >= limit)
            return Errors.PlanLimit("user(s)", limit);

        if (await users.LoginExistsAsync(c.Mobile.Trim(), c.Email.Trim().ToLowerInvariant(), ct))
            return Errors.LoginTaken;

        var role = await users.GetRoleByCodeAsync(c.RoleCode, ct);
        if (role is null) return Errors.NotFound("Role", c.RoleCode);

        // USR-03: the branches must belong to this tenant.
        var ownBranches = (await platform.GetBranchesAsync(currentUser.TenantId, ct)).Select(b => b.Id).ToHashSet();
        if (c.BranchIds.Any(id => !ownBranches.Contains(id))) return Errors.BranchNotAllowed;

        var user = User.Create(currentUser.TenantId, c.FullName, c.Mobile, c.Email, hasher.Hash(c.Password),
            c.Pin is null ? null : hasher.Hash(c.Pin), role.Id, c.IsPharmacist, c.LicenceNumber, c.LicenceExpiry);

        var id = await users.AddAsync(user, c.BranchIds.Distinct(), ct);
        await audit.RecordAsync("Security", "UserCreated", id, newValue: $"{user.FullName} ({role.Code})", ct: ct);
        return id;
    }
}

#endregion

#region Command: disable a user

/// <summary>US-USR-5 / BR-USR-3: block a user at once. The user is kept for history, never deleted.</summary>
[RequiresPermission(Permissions.UsersManage)]
public sealed record DisableUserCommand(int UserId) : ICommand<Unit>;

internal sealed class DisableUserHandler(IUserRepository users, ICurrentUser currentUser, IAuditTrail audit)
    : IRequestHandler<DisableUserCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(DisableUserCommand c, CancellationToken ct)
    {
        if (c.UserId == currentUser.UserId)
            return Error.Conflict("User.SelfDisable", "You cannot disable your own account.");

        var user = await users.GetByIdAsync(c.UserId, ct);
        if (user is null) return Errors.NotFound("User", c.UserId);

        await users.DisableAsync(c.UserId, ct);
        await audit.RecordAsync("Security", "UserDisabled", c.UserId, newValue: user.FullName, ct: ct);
        return Unit.Value;
    }
}

#endregion

#region Query: list users

[RequiresPermission(Permissions.UsersManage)]
public sealed record GetUsersQuery : IQuery<IReadOnlyList<UserDto>>;

internal sealed class GetUsersHandler(IUserRepository users) : IRequestHandler<GetUsersQuery, IReadOnlyList<UserDto>>
{
    public async Task<Result<IReadOnlyList<UserDto>>> HandleAsync(GetUsersQuery request, CancellationToken ct) =>
        Result<IReadOnlyList<UserDto>>.Success(await users.GetAllAsync(ct));
}

#endregion
