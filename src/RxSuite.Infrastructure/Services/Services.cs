using Microsoft.AspNetCore.Identity;
using RxSuite.Application.Abstractions;
using RxSuite.Domain.Tax;

namespace RxSuite.Infrastructure.Services;

#region Clock

/// <summary>The real clock. "Today" is the date in Cairo (NFR-09), whatever the server's time zone.</summary>
internal sealed class SystemClock : IClock
{
    private static readonly TimeZoneInfo Cairo = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");

    public DateTime UtcNow => DateTime.UtcNow;
    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Cairo));
}

#endregion

#region Secret hashing

/// <summary>
/// ADAPTER over ASP.NET Core's built-in password hasher (PBKDF2 with a random salt per hash).
/// Used for passwords and PINs; nothing is ever stored in clear (NFR-06).
/// </summary>
internal sealed class SecretHasher : ISecretHasher
{
    private static readonly object NoUser = new();
    private readonly PasswordHasher<object> _hasher = new();

    public string Hash(string secret) => _hasher.HashPassword(NoUser, secret);

    public bool Verify(string hash, string secret) =>
        _hasher.VerifyHashedPassword(NoUser, hash, secret) != PasswordVerificationResult.Failed;
}

#endregion

#region ETA gateway placeholder

/// <summary>
/// Stands in until the real ETA adapter is written (signing + the ETA API, ACC-01/03).
/// It answers "unavailable", so documents stay safely in the queue and nothing is lost.
/// </summary>
internal sealed class UnconfiguredEtaGateway : IEtaGateway
{
    public Task<EtaSubmissionResult> SubmitAsync(EtaDocument document, CancellationToken cancellationToken) =>
        Task.FromResult(new EtaSubmissionResult(EtaOutcome.Unavailable));
}

#endregion
