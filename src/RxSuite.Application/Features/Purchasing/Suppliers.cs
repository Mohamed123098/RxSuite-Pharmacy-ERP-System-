using RxSuite.Application.Abstractions;
using RxSuite.Domain.Common;
using RxSuite.Domain.Identity;
using RxSuite.Domain.Purchasing;

namespace RxSuite.Application.Features.Purchasing;

// USE CASES: supplier records (PUR-01).

#region DTO

public sealed class SupplierDto
{
    public int Id { get; set; }
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public SupplierType Type { get; set; }
    public string Phone { get; set; } = string.Empty;
    public int PaymentTermDays { get; set; }
    public decimal CreditLimit { get; set; }
    public decimal Balance { get; set; }
}

#endregion

#region Command: create a supplier

[RequiresPermission(Permissions.PurchasingCreate)]
public sealed record CreateSupplierCommand(string NameEn, string NameAr, SupplierType Type, string TaxNumber,
    string Phone, int PaymentTermDays, decimal CreditLimit) : ICommand<int>;

internal sealed class CreateSupplierHandler(ISupplierRepository suppliers, ICurrentUser currentUser)
    : IRequestHandler<CreateSupplierCommand, int>
{
    public async Task<Result<int>> HandleAsync(CreateSupplierCommand c, CancellationToken ct)
    {
        var supplier = Supplier.Create(currentUser.TenantId, c.NameEn, c.NameAr, c.Type, c.TaxNumber, c.Phone,
            c.PaymentTermDays, c.CreditLimit);

        return await suppliers.AddAsync(supplier, ct);
    }
}

#endregion

#region Query: list suppliers

[RequiresPermission(Permissions.PurchasingCreate)]
public sealed record GetSuppliersQuery : IQuery<IReadOnlyList<SupplierDto>>;

internal sealed class GetSuppliersHandler(ISupplierRepository suppliers)
    : IRequestHandler<GetSuppliersQuery, IReadOnlyList<SupplierDto>>
{
    public async Task<Result<IReadOnlyList<SupplierDto>>> HandleAsync(GetSuppliersQuery request, CancellationToken ct) =>
        Result<IReadOnlyList<SupplierDto>>.Success(await suppliers.GetAllAsync(ct));
}

#endregion
