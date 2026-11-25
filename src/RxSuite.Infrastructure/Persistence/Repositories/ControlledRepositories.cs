using RxSuite.Application.Abstractions;
using RxSuite.Application.Features.Controlled;
using RxSuite.Domain.Controlled;
using RxSuite.Infrastructure.Persistence.Connection;

namespace RxSuite.Infrastructure.Persistence.Repositories;

#region Controlled register

/// <summary>
/// REPOSITORY for the controlled drugs register (BRD 8.7). It can only append: there is no update
/// or delete method, and the database trigger refuses both anyway (BR-CTL-2).
/// </summary>
internal sealed class ControlledRegisterRepository(IDbSession session, ICurrentUser currentUser)
    : StoredProcedureRepository(session, currentUser), IControlledRegisterRepository
{
    #region Commands

    public Task<bool> AddEntriesAsync(IReadOnlyList<ControlledRegisterEntry> entries, CancellationToken ct) =>
        ExecuteScalarAsync<bool>("dbo.usp_ControlledRegister_Insert", Scoped(new
        {
            EntriesJson = Json(entries.Select(e => new
            {
                e.BranchId, e.ItemId, e.BatchId, Type = (byte)e.Type, e.Quantity, e.OccurredAtUtc, e.DocumentType,
                e.DocumentId, e.PatientId, e.PatientName, e.PatientNationalId, e.PrescriberName, e.PrescriberLicence,
                e.PrescriptionId, e.PrescriptionNumber, e.PrescriptionDate, e.PrescriptionImageId, e.PharmacistId,
                e.CorrectsEntryId, e.Note
            }))
        }), ct);

    public async Task<int> AddBalanceCheckAsync(ControlledBalanceCheck c, CancellationToken ct)
    {
        var id = await ExecuteScalarAsync<int>("dbo.usp_ControlledBalanceCheck_Insert", Scoped(new
        {
            c.BranchId, c.ItemId, c.CountedQuantity, c.RegisterBalance, c.StockQuantity, c.IsMismatch, c.PharmacistId,
            c.Note, c.CheckedAtUtc
        }), ct);

        c.AssignId(id);
        return id;
    }

    #endregion

    #region Queries

    public Task<ControlledRegisterEntry?> GetEntryAsync(long entryId, CancellationToken ct) =>
        QuerySingleOrDefaultAsync<ControlledRegisterEntry>("dbo.usp_ControlledRegister_GetById", Scoped(new { EntryId = entryId }), ct);

    public Task<int> GetBalanceAsync(int branchId, int itemId, CancellationToken ct) =>
        ExecuteScalarAsync<int>("dbo.usp_ControlledRegister_GetBalance", Scoped(new { BranchId = branchId, ItemId = itemId }), ct);

    public Task<int> GetStockQuantityAsync(int branchId, int itemId, CancellationToken ct) =>
        ExecuteScalarAsync<int>("dbo.usp_ControlledRegister_GetStockQuantity", Scoped(new { BranchId = branchId, ItemId = itemId }), ct);

    public Task<int> GetDispensedToPatientAsync(int patientId, int itemId, DateTime sinceUtc, CancellationToken ct) =>
        ExecuteScalarAsync<int>("dbo.usp_ControlledRegister_GetDispensedToPatient",
            Scoped(new { PatientId = patientId, ItemId = itemId, SinceUtc = sinceUtc }), ct);

    public Task<IReadOnlyList<RegisterLineDto>> GetEntriesAsync(int branchId, int itemId, DateOnly from, DateOnly to, CancellationToken ct) =>
        QueryAsync<RegisterLineDto>("dbo.usp_ControlledRegister_GetEntries",
            Scoped(new { BranchId = branchId, ItemId = itemId, From = from, To = to }), ct);

    public Task<IReadOnlyList<RegisterMonthLineDto>> GetMonthlySummaryAsync(int branchId, int year, int month, CancellationToken ct) =>
        QueryAsync<RegisterMonthLineDto>("dbo.usp_ControlledRegister_GetMonthlySummary",
            Scoped(new { BranchId = branchId, Year = year, Month = month }), ct);

    public Task<IReadOnlyList<ControlledItemStatusDto>> GetStatusAsync(int branchId, CancellationToken ct) =>
        QueryAsync<ControlledItemStatusDto>("dbo.usp_ControlledRegister_GetStatus", Scoped(new { BranchId = branchId }), ct);

    #endregion
}

#endregion
