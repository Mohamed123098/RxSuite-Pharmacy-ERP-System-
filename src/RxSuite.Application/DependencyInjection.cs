using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using RxSuite.Application.Abstractions;
using RxSuite.Application.Common;
using RxSuite.Application.Features.Clinical;
using RxSuite.Application.Features.Sales;
using RxSuite.Application.Pipeline;

namespace RxSuite.Application;

/// <summary>
/// Registers everything the Application layer owns. The Api calls one method and does not need
/// to know which classes exist here (each layer wires itself).
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        AddPipeline(services);
        AddUseCases(services);
        AddServices(services);
        return services;
    }

    #region Pipeline (Mediator, guards, decorator)

    private static void AddPipeline(IServiceCollection services)
    {
        services.AddScoped<IDispatcher, Dispatcher>();
        services.AddScoped(typeof(RequestPipeline<,>));

        // The guards run in this order for every request.
        services.AddScoped<IRequestGuard, PermissionGuard>();
        services.AddScoped<IRequestGuard, ActiveUserGuard>();
        services.AddScoped<IRequestGuard, TenantWriteGuard>();
    }

    #endregion

    #region Use cases (handlers and validators found by convention)

    /// <summary>
    /// Scans this assembly and registers every handler and validator against its interface, so
    /// adding a use case needs no change here (Open/Closed).
    /// </summary>
    private static void AddUseCases(IServiceCollection services)
    {
        Type[] contracts = [typeof(IRequestHandler<,>), typeof(IValidator<>)];

        var implementations = Assembly.GetExecutingAssembly().GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false });

        foreach (var implementation in implementations)
            foreach (var contract in implementation.GetInterfaces()
                         .Where(i => i.IsGenericType && contracts.Contains(i.GetGenericTypeDefinition())))
                services.AddScoped(contract, implementation);
    }

    #endregion

    #region Application services

    private static void AddServices(IServiceCollection services)
    {
        services.AddScoped<IAuditTrail, AuditTrail>();
        services.AddScoped<IOverrideAuthorizer, OverrideAuthorizer>();
        services.AddScoped<IPharmacistCheck, PharmacistCheck>();
        services.AddScoped<IClinicalCheckService, ClinicalCheckService>();
        services.AddScoped<IDispensingPlanner, DispensingPlanner>();
    }

    #endregion
}
