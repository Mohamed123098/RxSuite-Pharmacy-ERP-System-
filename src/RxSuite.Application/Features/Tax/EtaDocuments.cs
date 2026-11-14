using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Domain.Common;
using RxSuite.Domain.Identity;
using RxSuite.Domain.Tax;

namespace RxSuite.Application.Features.Tax;

// USE CASES: the ETA documents monitor (ACC-04, ACC-05), resubmitting a rejected document
// (AC-ACC-1) and the background submission of the queue (ACC-03, ACC-07; Figure 15).

#region DTO

public sealed class EtaDocumentDto
{
    public long Id { get; set; }
    public int BranchId { get; set; }
    public EtaDocumentType Type { get; set; }
    public long? SaleId { get; set; }
    public long? SaleReturnId { get; set; }
    public EtaDocumentStatus Status { get; set; }
    public string? Uuid { get; set; }
    public string? Errors { get; set; }
    public int Attempts { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
}

#endregion

#region Query: documents monitor

[RequiresPermission(Permissions.EtaManage)]
public sealed record GetEtaDocumentsQuery(EtaDocumentStatus? Status = null, int PageNumber = 1, int PageSize = 50)
    : IQuery<PagedResult<EtaDocumentDto>>;

internal sealed class GetEtaDocumentsHandler(IEtaDocumentRepository documents)
    : IRequestHandler<GetEtaDocumentsQuery, PagedResult<EtaDocumentDto>>
{
    public async Task<Result<PagedResult<EtaDocumentDto>>> HandleAsync(GetEtaDocumentsQuery q, CancellationToken ct)
    {
        if (q.PageNumber < 1 || q.PageSize is < 1 or > 200)
            return Error.Validation("Paging.Invalid", "Page number starts at 1 and page size is 1 to 200.");

        return await documents.GetPagedAsync(q.Status, q.PageNumber, q.PageSize, ct);
    }
}

#endregion

#region Command: resubmit a rejected document

/// <summary>AC-ACC-1: after the accountant fixes the data, the document goes back to the queue.</summary>
[RequiresPermission(Permissions.EtaManage)]
public sealed record ResubmitEtaDocumentCommand(long DocumentId) : ICommand<Unit>;

internal sealed class ResubmitEtaDocumentHandler(IEtaDocumentRepository documents, IAuditTrail audit)
    : IRequestHandler<ResubmitEtaDocumentCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(ResubmitEtaDocumentCommand c, CancellationToken ct)
    {
        var document = await documents.GetByIdAsync(c.DocumentId, ct);
        if (document is null) return Errors.NotFound("EtaDocument", c.DocumentId);

        document.Requeue();   // only allowed from Invalid (Figure 15)
        await documents.UpdateAsync(document, ct);
        await audit.RecordAsync("ETA", "Resubmitted", document.Id, branchId: document.BranchId, ct: ct);
        return Unit.Value;
    }
}

#endregion

#region Command: submit the queue (background job)

/// <summary>Sends the oldest queued documents to ETA. Returns how many reached a final answer.</summary>
[NoUserRequired]
public sealed record SubmitQueuedEtaDocumentsCommand(int BatchSize = 50) : ICommand<int>;

internal sealed class SubmitQueuedEtaDocumentsHandler(IEtaDocumentRepository documents, IEtaGateway gateway, IClock clock)
    : IRequestHandler<SubmitQueuedEtaDocumentsCommand, int>
{
    public async Task<Result<int>> HandleAsync(SubmitQueuedEtaDocumentsCommand c, CancellationToken ct)
    {
        var answered = 0;

        foreach (var document in await documents.GetQueuedAsync(c.BatchSize, ct))
        {
            var result = await gateway.SubmitAsync(document, ct);

            switch (result.Outcome)
            {
                case EtaOutcome.Accepted:
                    document.MarkValid(result.Uuid ?? string.Empty, result.LongId, result.QrCode, clock.UtcNow);
                    answered++;
                    break;
                case EtaOutcome.Rejected:
                    document.MarkInvalid(result.Errors ?? "Rejected by ETA.", clock.UtcNow);
                    answered++;
                    break;
                default:
                    document.RegisterFailedAttempt();   // stays queued; retried on the next run (ACC-07)
                    break;
            }

            await documents.UpdateAsync(document, ct);
        }

        return answered;
    }
}

#endregion
