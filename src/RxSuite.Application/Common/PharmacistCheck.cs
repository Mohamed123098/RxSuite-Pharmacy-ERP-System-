using RxSuite.Application.Abstractions;
using RxSuite.Domain.Common;
using RxSuite.Domain.Identity;

namespace RxSuite.Application.Common;

#region Contract

/// <summary>
/// CTL-02 / BR-CTL-1: some actions need the logged-in user themselves to be a licensed pharmacist.
/// A PIN from someone else is not enough for these (unlike USR-07 overrides).
/// </summary>
public interface IPharmacistCheck
{
    /// <summary>The logged-in user when they are a pharmacist with a valid licence and the rx.dispense permission.</summary>
    Task<Result<User>> CurrentAsync(CancellationToken ct);

    /// <summary>The same, and the PIN typed is the user's own: their signature (CTL-06).</summary>
    Task<Result<User>> CurrentWithSignatureAsync(string? pin, CancellationToken ct);
}

#endregion

#region Implementation

internal sealed class PharmacistCheck(IUserRepository users, ISecretHasher hasher, ICurrentUser currentUser, IClock clock)
    : IPharmacistCheck
{
    public async Task<Result<User>> CurrentAsync(CancellationToken ct)
    {
        if (!currentUser.Has(Permissions.RxDispense)) return Errors.PharmacistOnly;

        var user = await users.GetByIdAsync(currentUser.UserId, ct);
        return user is not null && user.CanActAsPharmacist(clock.Today) ? user : Errors.PharmacistOnly;
    }

    public async Task<Result<User>> CurrentWithSignatureAsync(string? pin, CancellationToken ct)
    {
        var pharmacist = await CurrentAsync(ct);
        if (pharmacist.IsFailure) return pharmacist;

        return pharmacist.Value.PinHash is { } hash && !string.IsNullOrWhiteSpace(pin) && hasher.Verify(hash, pin)
            ? pharmacist
            : Errors.OverrideRefused("Sign with your own PIN to confirm the count.");
    }
}

#endregion
