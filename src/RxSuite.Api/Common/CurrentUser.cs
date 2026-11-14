using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.BearerToken;
using RxSuite.Application.Abstractions;
using RxSuite.Application.Features.Identity;

namespace RxSuite.Api.Common;

#region Claims

/// <summary>Names of the claims stored in the access token, and the code that writes them.</summary>
internal static class TokenClaims
{
    public const string UserId = "uid";
    public const string TenantId = "tid";
    public const string UserName = "name";
    public const string Role = "role";
    public const string MaxDiscount = "maxdisc";
    public const string AllBranches = "allbr";
    public const string Branch = "br";
    public const string Permission = "perm";

    /// <summary>Turns the result of a successful login into the identity carried by the token.</summary>
    public static ClaimsPrincipal ToPrincipal(AuthenticatedUser user)
    {
        var claims = new List<Claim>
        {
            new(UserId, user.UserId.ToString(CultureInfo.InvariantCulture)),
            new(TenantId, user.TenantId.ToString(CultureInfo.InvariantCulture)),
            new(UserName, user.FullName),
            new(Role, user.RoleCode),
            new(MaxDiscount, user.MaxDiscountPercent.ToString(CultureInfo.InvariantCulture)),
            new(AllBranches, user.HasAllBranches ? "1" : "0")
        };
        claims.AddRange(user.BranchIds.Select(id => new Claim(Branch, id.ToString(CultureInfo.InvariantCulture))));
        claims.AddRange(user.Permissions.Select(permission => new Claim(Permission, permission)));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, BearerTokenDefaults.AuthenticationScheme));
    }
}

#endregion

#region Current user (adapter for ICurrentUser)

/// <summary>
/// ADAPTER. Gives the Application layer the logged-in user without it knowing about HTTP.
/// In a background job there is no HTTP request, so the user is simply "not authenticated".
/// </summary>
internal sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
    public int TenantId => Int(TokenClaims.TenantId);
    public int UserId => Int(TokenClaims.UserId);
    public string UserName => Principal?.FindFirstValue(TokenClaims.UserName) ?? string.Empty;

    public decimal MaxDiscountPercent =>
        decimal.TryParse(Principal?.FindFirstValue(TokenClaims.MaxDiscount), NumberStyles.Number,
            CultureInfo.InvariantCulture, out var value) ? value : 0m;

    public bool Has(string permission) => Principal?.HasClaim(TokenClaims.Permission, permission) == true;

    public bool CanAccessBranch(int branchId) =>
        Principal?.HasClaim(TokenClaims.AllBranches, "1") == true
        || Principal?.HasClaim(TokenClaims.Branch, branchId.ToString(CultureInfo.InvariantCulture)) == true;

    private int Int(string claimType) =>
        int.TryParse(Principal?.FindFirstValue(claimType), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
}

#endregion
