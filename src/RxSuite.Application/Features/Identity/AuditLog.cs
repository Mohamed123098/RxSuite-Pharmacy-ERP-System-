using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Domain.Common;
using RxSuite.Domain.Identity;

namespace RxSuite.Application.Features.Identity;

// USE CASE (USR-12): the audit log viewer with filters. Entries can only be read.

#region DTO

public sealed class AuditEntryDto
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public int? UserId { get; set; }
    public string? UserName { get; set; }
    public int? BranchId { get; set; }
    public string Module { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? RecordId { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? Reason { get; set; }
}

#endregion

#region Query

[RequiresPermission(Permissions.AuditView)]
public sealed record GetAuditLogQuery(int PageNumber = 1, int PageSize = 50, int? UserId = null, int? BranchId = null,
    string? Module = null, DateTime? FromUtc = null, DateTime? ToUtc = null) : IQuery<PagedResult<AuditEntryDto>>;

internal sealed class GetAuditLogValidator : IValidator<GetAuditLogQuery>
{
    public IReadOnlyList<string> Validate(GetAuditLogQuery q)
    {
        var problems = new List<string>();
        if (q.PageNumber < 1) problems.Add("Page number starts at 1.");
        if (q.PageSize is < 1 or > 200) problems.Add("Page size must be between 1 and 200.");
        return problems;
    }
}

#endregion

#region Handler

internal sealed class GetAuditLogHandler(IAuditRepository audit) : IRequestHandler<GetAuditLogQuery, PagedResult<AuditEntryDto>>
{
    public async Task<Result<PagedResult<AuditEntryDto>>> HandleAsync(GetAuditLogQuery request, CancellationToken ct) =>
        await audit.GetPagedAsync(request, ct);
}

#endregion
