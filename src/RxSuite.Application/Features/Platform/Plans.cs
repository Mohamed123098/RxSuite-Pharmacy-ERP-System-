using RxSuite.Application.Abstractions;
using RxSuite.Domain.Common;

namespace RxSuite.Application.Features.Platform;

// USE CASE (SUB-03): show the plans on the public pricing page.

#region DTO

public sealed record PlanDto(int Id, string Code, string NameEn, string NameAr, decimal MonthlyPricePerBranch,
    decimal YearlyPricePerBranch, int? MaxBranches, int? MaxUsersPerBranch);

#endregion

#region Query

[NoUserRequired]
public sealed record GetPlansQuery : IQuery<IReadOnlyList<PlanDto>>;

#endregion

#region Handler

internal sealed class GetPlansHandler(IPlatformRepository platform, Domain.Rules.BusinessRules rules)
    : IRequestHandler<GetPlansQuery, IReadOnlyList<PlanDto>>
{
    public async Task<Result<IReadOnlyList<PlanDto>>> HandleAsync(GetPlansQuery request, CancellationToken ct)
    {
        var plans = await platform.GetPlansAsync(ct);

        return plans.Where(p => p.IsActive)
            .Select(p => new PlanDto(p.Id, p.Code, p.NameEn, p.NameAr, p.MonthlyPricePerBranch,
                p.MonthlyPricePerBranch * rules.Subscription.YearlyPriceMultiplier,   // BR-SUB-3
                p.MaxBranches, p.MaxUsersPerBranch))
            .ToList();
    }
}

#endregion
