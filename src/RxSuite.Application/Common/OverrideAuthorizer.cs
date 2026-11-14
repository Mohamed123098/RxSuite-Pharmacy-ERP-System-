using RxSuite.Application.Abstractions;
using RxSuite.Domain.Common;
using RxSuite.Domain.Identity;

namespace RxSuite.Application.Common;

#region Request

/// <summary>A second user approving an action on the first user's screen with their PIN (USR-07).</summary>
public sealed record ManagerOverride(int ApproverUserId, string Pin);

#endregion

#region Contract

public interface IOverrideAuthorizer
{
    /// <summary>Checks that the approver may grant <paramref name="permission"/> and that the PIN is right.</summary>
    Task<Result<User>> AuthorizeAsync(ManagerOverride? request, string permission, CancellationToken ct);

    /// <summary>BR-USR-4: checks that the approver is a licensed pharmacist with a valid licence.</summary>
    Task<Result<User>> AuthorizePharmacistAsync(ManagerOverride? request, CancellationToken ct);
}

#endregion

#region Implementation

internal sealed class OverrideAuthorizer(
    IUserRepository users, ISecretHasher hasher, ICurrentUser currentUser, IClock clock) : IOverrideAuthorizer
{
    public Task<Result<User>> AuthorizeAsync(ManagerOverride? request, string permission, CancellationToken ct) =>
        CheckAsync(request, permission, requirePharmacist: false, ct);

    public Task<Result<User>> AuthorizePharmacistAsync(ManagerOverride? request, CancellationToken ct) =>
        CheckAsync(request, Permissions.RxDispense, requirePharmacist: true, ct);

    private async Task<Result<User>> CheckAsync(ManagerOverride? request, string permission, bool requirePharmacist, CancellationToken ct)
    {
        if (request is null)
            return Errors.OverrideRefused(requirePharmacist
                ? "A licensed pharmacist must approve this sale with their PIN."
                : "A manager must approve this action with their PIN.");

        // BR-USR-2: a user cannot approve their own request.
        if (request.ApproverUserId == currentUser.UserId)
            return Errors.OverrideRefused("You cannot approve your own request.");

        // The same answer for "no such user" and "wrong PIN", so the PIN cannot be probed.
        var approver = await users.GetByIdAsync(request.ApproverUserId, ct);
        if (approver is not { IsActive: true, PinHash: not null } || !hasher.Verify(approver.PinHash, request.Pin))
            return Errors.OverrideRefused("The approver or the PIN is not correct.");

        var role = await users.GetRoleAsync(approver.RoleId, ct);
        if (role is null || !role.Has(permission))
            return Errors.OverrideRefused("The approver is not allowed to approve this action.");

        if (requirePharmacist && !approver.CanActAsPharmacist(clock.Today))
            return Errors.OverrideRefused("The approver is not a pharmacist with a valid licence.");

        return approver;
    }
}

#endregion
