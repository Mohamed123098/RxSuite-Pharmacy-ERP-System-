using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Domain.Common;
using RxSuite.Domain.Identity;
using RxSuite.Domain.Platform;
using RxSuite.Domain.Rules;

namespace RxSuite.Application.Features.Platform;

// USE CASES: branches and warehouses of the tenant (MBR-01), within the plan limit (SUB-12).

#region DTO

public sealed record BranchDto(int Id, string NameEn, string NameAr, BranchType Type, string Address, bool IsActive);

#endregion

#region Command: add a branch

[RequiresPermission(Permissions.BranchesManage)]
public sealed record AddBranchCommand(string NameEn, string NameAr, BranchType Type, string Address) : ICommand<int>;

internal sealed class AddBranchHandler(IPlatformRepository platform, ICurrentUser currentUser, IAuditTrail audit, BusinessRules rules)
    : IRequestHandler<AddBranchCommand, int>
{
    public async Task<Result<int>> HandleAsync(AddBranchCommand c, CancellationToken ct)
    {
        var subscription = await platform.GetSubscriptionAsync(currentUser.TenantId, ct);
        if (subscription is null) return Errors.SubscriptionNotFound;

        // BR-SUB-2: only selling branches count against the plan; warehouses are free.
        if (c.Type == BranchType.Branch)
        {
            var limit = subscription.MaxBranches(rules.Subscription);
            var used = (await platform.GetBranchesAsync(currentUser.TenantId, ct))
                .Count(b => b is { IsActive: true, Type: BranchType.Branch });

            if (used >= limit) return Errors.PlanLimit("branch(es)", limit);
        }

        var branch = Branch.Create(currentUser.TenantId, c.NameEn, c.NameAr, c.Type, c.Address);
        var id = await platform.AddBranchAsync(branch, ct);

        await audit.RecordAsync("Platform", "BranchAdded", id, newValue: branch.NameEn, ct: ct);
        return id;
    }
}

#endregion

#region Query: list branches

public sealed record GetBranchesQuery : IQuery<IReadOnlyList<BranchDto>>;

internal sealed class GetBranchesHandler(IPlatformRepository platform, ICurrentUser currentUser)
    : IRequestHandler<GetBranchesQuery, IReadOnlyList<BranchDto>>
{
    public async Task<Result<IReadOnlyList<BranchDto>>> HandleAsync(GetBranchesQuery request, CancellationToken ct)
    {
        var branches = await platform.GetBranchesAsync(currentUser.TenantId, ct);

        return branches
            .Where(b => currentUser.CanAccessBranch(b.Id))   // USR-03
            .Select(b => new BranchDto(b.Id, b.NameEn, b.NameAr, b.Type, b.Address, b.IsActive))
            .ToList();
    }
}

#endregion
