using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Domain.Common;
using RxSuite.Domain.Rules;

namespace RxSuite.Application.Features.Identity;

// USE CASE (USR-04): log in with mobile or e-mail and a password, with lockout after 5 wrong
// attempts. Not built yet: two-factor OTP (USR-05), quick-switch PIN login (USR-06).

#region Password policy

/// <summary>USR-04: minimum length, and both letters and numbers.</summary>
public static class PasswordPolicy
{
    public static IEnumerable<string> Check(string? password, SecurityRules rules)
    {
        if (string.IsNullOrEmpty(password) || password.Length < rules.MinPasswordLength)
            yield return $"The password needs at least {rules.MinPasswordLength} characters.";
        else if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            yield return "The password needs both letters and numbers.";
    }
}

#endregion

#region Command and result

/// <summary>Non-transactional on purpose: a failed attempt must be counted even though the login fails.</summary>
[NoUserRequired, NonTransactional]
public sealed record LoginCommand(string Login, string Password) : ICommand<AuthenticatedUser>;

/// <summary>Everything the Api layer puts into the access token.</summary>
public sealed record AuthenticatedUser(int UserId, int TenantId, string FullName, string RoleCode,
    decimal MaxDiscountPercent, bool HasAllBranches, IReadOnlyList<int> BranchIds, IReadOnlyCollection<string> Permissions);

#endregion

#region Handler

internal sealed class LoginHandler(IUserRepository users, ISecretHasher hasher, IAuditTrail audit, IClock clock, BusinessRules rules)
    : IRequestHandler<LoginCommand, AuthenticatedUser>
{
    public async Task<Result<AuthenticatedUser>> HandleAsync(LoginCommand c, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.Login) || string.IsNullOrEmpty(c.Password)) return Errors.InvalidLogin;

        // Unknown and disabled users get the same answer as a wrong password.
        var user = await users.GetByLoginAsync(c.Login.Trim().ToLowerInvariant(), ct);
        if (user is not { IsActive: true }) return Errors.InvalidLogin;

        var now = clock.UtcNow;
        if (user.IsLockedOut(now)) return Errors.AccountLocked;

        if (!hasher.Verify(user.PasswordHash, c.Password))
        {
            user.RegisterFailedLogin(now, rules.Security);
            await users.SaveLoginStateAsync(user, ct);
            await audit.RecordForAsync(user.TenantId, user.Id, "Security", "LoginFailed", user.Id, ct: ct);
            return user.IsLockedOut(now) ? Errors.AccountLocked : Errors.InvalidLogin;
        }

        user.RegisterSuccessfulLogin(now);
        await users.SaveLoginStateAsync(user, ct);
        await audit.RecordForAsync(user.TenantId, user.Id, "Security", "Login", user.Id, ct: ct);

        var role = await users.GetRoleAsync(user.RoleId, ct);
        if (role is null) return Errors.NotFound("Role", user.RoleId);

        return new AuthenticatedUser(user.Id, user.TenantId, user.FullName, role.Code, role.MaxDiscountPercent,
            role.HasAllBranches, await users.GetBranchIdsAsync(user.TenantId, user.Id, ct), role.Permissions);
    }
}

#endregion
