using RxSuite.Domain.Tax;

namespace RxSuite.Application.Abstractions;

// PORTS (Dependency Inversion): what the use cases need from the outside world.
// The Api and Infrastructure layers provide the adapters.

#region Who is calling

/// <summary>The logged-in user of the current request. Filled from the access token by the Api layer.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    int TenantId { get; }
    int UserId { get; }
    string UserName { get; }

    /// <summary>POS-07: the largest discount this user may give alone.</summary>
    decimal MaxDiscountPercent { get; }
    bool Has(string permission);

    /// <summary>USR-03: data and actions are limited to the user's branches.</summary>
    bool CanAccessBranch(int branchId);
}

#endregion

#region Time

/// <summary>The clock, behind an interface so tests can fix the date.</summary>
public interface IClock
{
    DateTime UtcNow { get; }

    /// <summary>Today's date in Cairo (NFR-09), used for expiry, trials and renewals.</summary>
    DateOnly Today { get; }
}

#endregion

#region Security

/// <summary>Hashes and verifies passwords and PINs (NFR-06: never stored in clear).</summary>
public interface ISecretHasher
{
    string Hash(string secret);
    bool Verify(string hash, string secret);
}

#endregion

#region Egyptian Tax Authority

public enum EtaOutcome { Accepted, Rejected, Unavailable }

/// <summary>What ETA answered for one document.</summary>
public sealed record EtaSubmissionResult(EtaOutcome Outcome, string? Uuid = null, string? LongId = null,
    string? QrCode = null, string? Errors = null);

/// <summary>
/// ADAPTER port for the ETA e-receipt / e-invoice API (ACC-03). The real adapter signs the
/// document and calls ETA; it is not part of this build (see README).
/// </summary>
public interface IEtaGateway
{
    Task<EtaSubmissionResult> SubmitAsync(EtaDocument document, CancellationToken cancellationToken);
}

#endregion
