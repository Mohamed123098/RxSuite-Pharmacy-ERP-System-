using RxSuite.Application.Common;
using RxSuite.Application.Features.Catalog;
using RxSuite.Application.Features.Clinical;
using RxSuite.Application.Features.Controlled;
using RxSuite.Application.Features.Identity;
using RxSuite.Application.Features.Inventory;
using RxSuite.Application.Features.Platform;
using RxSuite.Application.Features.Purchasing;
using RxSuite.Application.Features.Tax;
using RxSuite.Application.Features.Transfers;
using RxSuite.Domain.Catalog;
using RxSuite.Domain.Clinical;
using RxSuite.Domain.Controlled;
using RxSuite.Domain.Identity;
using RxSuite.Domain.Inventory;
using RxSuite.Domain.Platform;
using RxSuite.Domain.Purchasing;
using RxSuite.Domain.Sales;
using RxSuite.Domain.Tax;
using RxSuite.Domain.Transfers;

namespace RxSuite.Application.Abstractions;

// REPOSITORY PATTERN + DEPENDENCY INVERSION. The Application layer owns these interfaces; the
// Infrastructure layer implements them with Dapper and stored procedures.
// Write side: methods take and return domain entities. Read side (CQRS): methods return DTOs
// shaped for the screen, straight from a stored procedure.
// TENANT ISOLATION (NFR-05): unless a method takes a tenantId, the repository adds the tenant of
// the logged-in user to every call by itself, so a use case cannot forget it.

#region Unit of Work

/// <summary>UNIT OF WORK. Groups the repository calls of one command into one transaction.</summary>
public interface IUnitOfWork
{
    Task BeginAsync(CancellationToken cancellationToken);
    Task CommitAsync(CancellationToken cancellationToken);
    Task RollbackAsync(CancellationToken cancellationToken);
}

#endregion

#region Platform (plans, tenants, subscriptions, branches)

/// <summary>Platform-level data. Methods take the tenant explicitly: several run before anyone is logged in.</summary>
public interface IPlatformRepository
{
    Task<IReadOnlyList<Plan>> GetPlansAsync(CancellationToken ct);
    Task<int> AddTenantAsync(Tenant tenant, CancellationToken ct);
    Task<TenantStatus?> GetTenantStatusAsync(int tenantId, CancellationToken ct);
    Task SetTenantStatusAsync(int tenantId, TenantStatus status, CancellationToken ct);
    Task<int> AddSubscriptionAsync(Subscription subscription, CancellationToken ct);
    Task<Subscription?> GetSubscriptionAsync(int tenantId, CancellationToken ct);
    Task UpdateSubscriptionAsync(Subscription subscription, CancellationToken ct);
    Task AddSubscriptionPaymentAsync(int tenantId, decimal amount, string method, string gatewayReference, CancellationToken ct);
    Task<IReadOnlyList<SubscriptionState>> GetSubscriptionStatesAsync(CancellationToken ct);
    Task<int> AddBranchAsync(Branch branch, CancellationToken ct);
    Task<IReadOnlyList<Branch>> GetBranchesAsync(int tenantId, CancellationToken ct);
}

#endregion

#region Identity (users, roles, audit)

public interface IUserRepository
{
    Task<int> AddAsync(User user, IEnumerable<int> branchIds, CancellationToken ct);
    /// <summary>Finds a user by mobile or e-mail across all tenants (used only by login).</summary>
    Task<User?> GetByLoginAsync(string login, CancellationToken ct);
    Task<bool> LoginExistsAsync(string mobile, string email, CancellationToken ct);
    Task<User?> GetByIdAsync(int userId, CancellationToken ct);
    Task SaveLoginStateAsync(User user, CancellationToken ct);
    Task DisableAsync(int userId, CancellationToken ct);
    Task<int> CountActiveAsync(CancellationToken ct);
    Task<IReadOnlyList<int>> GetBranchIdsAsync(int tenantId, int userId, CancellationToken ct);
    Task<Role?> GetRoleAsync(int roleId, CancellationToken ct);
    Task<Role?> GetRoleByCodeAsync(string code, CancellationToken ct);
    Task<IReadOnlyList<UserDto>> GetAllAsync(CancellationToken ct);
}

public interface IAuditRepository
{
    Task AddAsync(AuditEntry entry, CancellationToken ct);
    Task<PagedResult<AuditEntryDto>> GetPagedAsync(GetAuditLogQuery filter, CancellationToken ct);
}

#endregion

#region Catalog and inventory

public interface IItemRepository
{
    Task<int> AddAsync(Item item, IEnumerable<string> barcodes, CancellationToken ct);
    Task<bool> CodeExistsAsync(string code, CancellationToken ct);
    Task<Item?> GetByIdAsync(int itemId, CancellationToken ct);
    Task<IReadOnlyList<Item>> GetByIdsAsync(IEnumerable<int> itemIds, CancellationToken ct);
    Task UpdatePriceAsync(Item item, CancellationToken ct);
    Task<IReadOnlyList<ItemSearchResult>> SearchAsync(int branchId, string term, DateOnly today, CancellationToken ct);
}

public interface IStockRepository
{
    Task<IReadOnlyList<Batch>> GetBatchesAsync(int branchId, IEnumerable<int> itemIds, CancellationToken ct);
    Task<Batch?> GetBatchAsync(int batchId, CancellationToken ct);

    /// <summary>
    /// Applies signed movements to existing batches and writes the stock card.
    /// Returns false, changing nothing, when any batch would go below zero.
    /// </summary>
    Task<bool> ApplyMovementsAsync(int branchId, IEnumerable<StockMovement> movements, CancellationToken ct);

    /// <summary>Adds received stock to its batch (created when new) and returns the batch Id.</summary>
    Task<int> ReceiveBatchAsync(int branchId, int itemId, string batchNumber, DateOnly expiryDate, int quantity,
        decimal unitCost, int goodsReceiptId, CancellationToken ct);

    /// <summary>BR-MBR-2: adds transferred stock to the same batch (number + expiry) at the destination; returns its Id.</summary>
    Task<int> ReceiveTransferBatchAsync(int branchId, int itemId, string batchNumber, DateOnly expiryDate, int quantity,
        decimal unitCost, int transferId, CancellationToken ct);

    Task<int> RecallBatchAsync(int itemId, string batchNumber, CancellationToken ct);
    /// <summary>INV-08: quarantines every expired batch of every tenant. Returns how many.</summary>
    Task<int> QuarantineExpiredAsync(DateOnly today, CancellationToken ct);
    Task<IReadOnlyList<StockCardLine>> GetStockCardAsync(int branchId, int itemId, CancellationToken ct);
    Task<IReadOnlyList<ExpiryAlert>> GetNearExpiryAsync(int branchId, DateOnly until, CancellationToken ct);
    Task<IReadOnlyList<LowStockAlert>> GetLowStockAsync(int branchId, DateOnly today, CancellationToken ct);
}

#endregion

#region Purchasing

public interface ISupplierRepository
{
    Task<int> AddAsync(Supplier supplier, CancellationToken ct);
    Task<Supplier?> GetByIdAsync(int supplierId, CancellationToken ct);
    Task<IReadOnlyList<SupplierDto>> GetAllAsync(CancellationToken ct);
    Task IncreaseBalanceAsync(int supplierId, decimal amount, CancellationToken ct);
}

public interface IPurchaseOrderRepository
{
    Task<int> AddAsync(PurchaseOrder order, CancellationToken ct);
    Task<PurchaseOrder?> GetByIdAsync(int orderId, CancellationToken ct);
    /// <summary>Saves the status, the approver and the received quantities.</summary>
    Task UpdateAsync(PurchaseOrder order, CancellationToken ct);
}

public interface IGoodsReceiptRepository
{
    Task<int> AddAsync(GoodsReceipt receipt, CancellationToken ct);
}

#endregion

#region Sales

public interface IShiftRepository
{
    Task<int> AddAsync(Shift shift, CancellationToken ct);
    Task<Shift?> GetOpenAsync(int branchId, int cashierId, CancellationToken ct);
    Task<Shift?> GetByIdAsync(int shiftId, CancellationToken ct);
    Task<ShiftCashTotals> GetCashTotalsAsync(int shiftId, CancellationToken ct);
    Task AddCashMovementAsync(int shiftId, CashMovementType type, decimal amount, string reason, CancellationToken ct);
    Task CloseAsync(Shift shift, CancellationToken ct);
}

public interface ISaleRepository
{
    Task<long?> GetIdByOfflineIdAsync(Guid offlineId, CancellationToken ct);
    Task<long> AddAsync(Sale sale, CancellationToken ct);
    Task<Sale?> GetByIdAsync(long saleId, CancellationToken ct);
    /// <summary>Saves the return and marks the returned quantities on the original receipt.</summary>
    Task<long> AddReturnAsync(SaleReturn saleReturn, CancellationToken ct);
}

#endregion

#region Tax (ETA)

public interface IEtaDocumentRepository
{
    Task<long> AddAsync(EtaDocument document, CancellationToken ct);
    Task<EtaDocument?> GetBySaleIdAsync(long saleId, CancellationToken ct);
    Task<EtaDocument?> GetByIdAsync(long documentId, CancellationToken ct);
    /// <summary>Saves the status fields. Uses the document's own tenant (called by the background job).</summary>
    Task UpdateAsync(EtaDocument document, CancellationToken ct);
    /// <summary>The oldest queued documents of all tenants (ACC-07).</summary>
    Task<IReadOnlyList<EtaDocument>> GetQueuedAsync(int max, CancellationToken ct);
    Task<PagedResult<EtaDocumentDto>> GetPagedAsync(EtaDocumentStatus? status, int pageNumber, int pageSize, CancellationToken ct);
}

#endregion

#region Drug database and clinical data (BRD 8.6)

/// <summary>
/// The central drug database (RX-01). It is shared by every tenant and updated centrally (RX-11),
/// so its procedures take no tenant; the tenant's items are matched to it through Item.DrugId.
/// </summary>
public interface IDrugRepository
{
    Task<DrugDto?> GetDrugAsync(int drugId, CancellationToken ct);
    Task<IReadOnlyList<DrugDto>> SearchDrugsAsync(string term, CancellationToken ct);

    /// <summary>The ingredients behind the tenant's items. Items not linked to a central drug are left out.</summary>
    Task<IReadOnlyList<ItemClinicalProfile>> GetClinicalProfilesAsync(IEnumerable<int> itemIds, CancellationToken ct);

    /// <summary>Every known interaction in which at least two of the given ingredients take part.</summary>
    Task<IReadOnlyList<DrugInteraction>> GetInteractionsAsync(IEnumerable<string> ingredientCodes, CancellationToken ct);
}

public interface IPatientRepository
{
    Task<int> AddAsync(Patient patient, CancellationToken ct);
    Task<Patient?> GetByIdAsync(int patientId, CancellationToken ct);
    Task<bool> NationalIdExistsAsync(string nationalId, CancellationToken ct);
    Task AddAllergyAsync(int patientId, PatientAllergy allergy, CancellationToken ct);
    Task<IReadOnlyList<PatientSummaryDto>> SearchAsync(string term, CancellationToken ct);

    /// <summary>RX-02: items dispensed to the patient since the date, in any branch, except from one prescription.</summary>
    Task<IReadOnlyList<int>> GetCurrentMedicationItemIdsAsync(int patientId, DateTime sinceUtc, int? exceptPrescriptionId,
        CancellationToken ct);

    /// <summary>RX-08: everything dispensed to the patient, newest first, across all branches of the tenant.</summary>
    Task<IReadOnlyList<MedicationHistoryLine>> GetMedicationHistoryAsync(int patientId, CancellationToken ct);
}

public interface IPrescriberRepository
{
    Task<int> AddAsync(Prescriber prescriber, CancellationToken ct);
    Task<Prescriber?> GetByIdAsync(int prescriberId, CancellationToken ct);
    Task<IReadOnlyList<PrescriberDto>> SearchAsync(string term, CancellationToken ct);
}

public interface IPrescriptionRepository
{
    Task<int> AddAsync(Prescription prescription, CancellationToken ct);
    Task<Prescription?> GetByIdAsync(int prescriptionId, CancellationToken ct);

    /// <summary>Saves the status and the dispensed quantity of every line.</summary>
    Task UpdateAsync(Prescription prescription, CancellationToken ct);
    Task<int> AddImageAsync(int prescriptionId, string storageKey, string contentType, int sizeBytes, int uploadedBy,
        DateTime uploadedAtUtc, CancellationToken ct);
    Task<StoredPrescriptionImage?> GetImageAsync(int prescriptionId, int imageId, CancellationToken ct);

    /// <summary>Prescriptions with something left to dispense ("refills due" screen), optionally for one patient.</summary>
    Task<IReadOnlyList<OpenPrescriptionDto>> GetOpenAsync(int? patientId, CancellationToken ct);
}

public interface IDispenseRepository
{
    Task<long> AddAsync(Dispense dispense, CancellationToken ct);

    /// <summary>RX-07: what the labels of a sale's dispensed medicines need.</summary>
    Task<IReadOnlyList<DispenseLabelRow>> GetLabelRowsAsync(long saleId, CancellationToken ct);
}

#endregion

#region Controlled drugs register (BRD 8.7)

public interface IControlledRegisterRepository
{
    /// <summary>
    /// Appends entries. The procedure works out each running balance under a lock; it returns
    /// false, writing nothing, when a balance would go below zero.
    /// </summary>
    Task<bool> AddEntriesAsync(IReadOnlyList<ControlledRegisterEntry> entries, CancellationToken ct);
    Task<ControlledRegisterEntry?> GetEntryAsync(long entryId, CancellationToken ct);
    Task<int> GetBalanceAsync(int branchId, int itemId, CancellationToken ct);

    /// <summary>Physical stock of the item in the branch, every batch status included.</summary>
    Task<int> GetStockQuantityAsync(int branchId, int itemId, CancellationToken ct);

    /// <summary>CTL-08: smallest units of the item dispensed to the patient since the date, net of corrections.</summary>
    Task<int> GetDispensedToPatientAsync(int patientId, int itemId, DateTime sinceUtc, CancellationToken ct);

    /// <summary>The register of one item between two Cairo dates, oldest first (CTL-04, CTL-07).</summary>
    Task<IReadOnlyList<RegisterLineDto>> GetEntriesAsync(int branchId, int itemId, DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>CTL-07: opening, in, out and closing per item for one month.</summary>
    Task<IReadOnlyList<RegisterMonthLineDto>> GetMonthlySummaryAsync(int branchId, int year, int month, CancellationToken ct);
    Task<int> AddBalanceCheckAsync(ControlledBalanceCheck check, CancellationToken ct);

    /// <summary>US-CTL-3: register balance, stock and last check of every controlled item in the branch.</summary>
    Task<IReadOnlyList<ControlledItemStatusDto>> GetStatusAsync(int branchId, CancellationToken ct);
}

#endregion

#region Multi-branch (BRD 8.8)

public interface ITransferRepository
{
    Task<int> AddAsync(Transfer transfer, CancellationToken ct);
    Task<Transfer?> GetByIdAsync(int transferId, CancellationToken ct);

    /// <summary>Saves the status, who did what and when, and the shipped batches with what was received.</summary>
    Task UpdateAsync(Transfer transfer, CancellationToken ct);
    Task<IReadOnlyList<TransferSummaryDto>> GetListAsync(int? branchId, TransferStatus? status, CancellationToken ct);
}

/// <summary>MBR-02: the per-branch price, min/max and visibility of items.</summary>
public interface IBranchItemSettingRepository
{
    /// <summary>Creates or replaces the setting of one item in one branch.</summary>
    Task SaveAsync(BranchItemSetting setting, CancellationToken ct);
    Task<IReadOnlyList<BranchItemSetting>> GetAsync(int branchId, IEnumerable<int> itemIds, CancellationToken ct);
}

/// <summary>Cross-branch read models for HQ (MBR-05, MBR-06, MBR-08).</summary>
public interface IBranchReportRepository
{
    /// <summary>Every batch of the matching items in every branch, plus what is in transit.</summary>
    Task<(IReadOnlyList<StockMatrixRow> Batches, IReadOnlyList<InTransitRow> InTransit)> GetStockMatrixAsync(string term,
        CancellationToken ct);

    Task<IReadOnlyList<RebalancingSuggestionDto>> GetRebalancingSuggestionsAsync(DateOnly today, DateOnly nearExpiryUntil,
        int salesWindowDays, CancellationToken ct);

    Task<IReadOnlyList<BranchPerformanceDto>> GetBranchPerformanceAsync(DateOnly from, DateOnly to, CancellationToken ct);
}

#endregion
