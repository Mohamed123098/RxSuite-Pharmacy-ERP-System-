using RxSuite.Application.Abstractions;
using RxSuite.Domain.Identity;

namespace RxSuite.Application.Common;

#region Contract

/// <summary>Writes the audit trail (USR-11). Handlers call it for the sensitive actions the BRD lists.</summary>
public interface IAuditTrail
{
    /// <summary>Records an action of the logged-in user.</summary>
    Task RecordAsync(string module, string action, object? recordId, string? oldValue = null,
        string? newValue = null, string? reason = null, int? branchId = null, CancellationToken ct = default);

    /// <summary>Records an action when nobody is logged in yet (sign-up, login).</summary>
    Task RecordForAsync(int tenantId, int? userId, string module, string action, object? recordId,
        string? newValue = null, CancellationToken ct = default);
}

#endregion

#region Implementation

internal sealed class AuditTrail(IAuditRepository repository, ICurrentUser currentUser, IClock clock) : IAuditTrail
{
    public Task RecordAsync(string module, string action, object? recordId, string? oldValue = null,
        string? newValue = null, string? reason = null, int? branchId = null, CancellationToken ct = default) =>
        repository.AddAsync(new AuditEntry(clock.UtcNow, currentUser.TenantId, currentUser.UserId, branchId,
            module, action, recordId?.ToString(), oldValue, newValue, reason), ct);

    public Task RecordForAsync(int tenantId, int? userId, string module, string action, object? recordId,
        string? newValue = null, CancellationToken ct = default) =>
        repository.AddAsync(new AuditEntry(clock.UtcNow, tenantId, userId, null, module, action,
            recordId?.ToString(), null, newValue, null), ct);
}

#endregion
