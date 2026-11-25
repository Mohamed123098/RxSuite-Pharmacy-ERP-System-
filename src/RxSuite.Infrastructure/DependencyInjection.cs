using Microsoft.Extensions.DependencyInjection;
using RxSuite.Application.Abstractions;
using RxSuite.Infrastructure.Persistence.Connection;
using RxSuite.Infrastructure.Persistence.Repositories;
using RxSuite.Infrastructure.Services;

namespace RxSuite.Infrastructure;

/// <summary>
/// Registers the Infrastructure layer: binds each Application interface to its implementation.
/// All implementation classes are internal, so nothing outside this project can depend on them.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString,
        PrescriptionImageOptions? prescriptionImages = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("The database connection string is missing.", nameof(connectionString));

        AddPersistence(services, connectionString);
        AddServices(services, prescriptionImages ?? new PrescriptionImageOptions());
        return services;
    }

    #region Persistence

    private static void AddPersistence(IServiceCollection services, string connectionString)
    {
        DateOnlyHandler.Register();
        services.AddSingleton<IDbConnectionFactory>(new SqlConnectionFactory(connectionString));

        // ONE DbSession per request, seen through two interfaces: repositories use IDbSession,
        // the pipeline uses IUnitOfWork. Same object, so all repositories share one transaction.
        services.AddScoped<DbSession>();
        services.AddScoped<IDbSession>(provider => provider.GetRequiredService<DbSession>());
        services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<DbSession>());

        services.AddScoped<IPlatformRepository, PlatformRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IAuditRepository, AuditRepository>();
        services.AddScoped<IItemRepository, ItemRepository>();
        services.AddScoped<IStockRepository, StockRepository>();
        services.AddScoped<ISupplierRepository, SupplierRepository>();
        services.AddScoped<IPurchaseOrderRepository, PurchaseOrderRepository>();
        services.AddScoped<IGoodsReceiptRepository, GoodsReceiptRepository>();
        services.AddScoped<IShiftRepository, ShiftRepository>();
        services.AddScoped<ISaleRepository, SaleRepository>();
        services.AddScoped<IEtaDocumentRepository, EtaDocumentRepository>();
        services.AddScoped<IDrugRepository, DrugRepository>();
        services.AddScoped<IPatientRepository, PatientRepository>();
        services.AddScoped<IPrescriberRepository, PrescriberRepository>();
        services.AddScoped<IPrescriptionRepository, PrescriptionRepository>();
        services.AddScoped<IDispenseRepository, DispenseRepository>();
        services.AddScoped<IControlledRegisterRepository, ControlledRegisterRepository>();
    }

    #endregion

    #region Services

    private static void AddServices(IServiceCollection services, PrescriptionImageOptions prescriptionImages)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ISecretHasher, SecretHasher>();
        services.AddSingleton<IEtaGateway, UnconfiguredEtaGateway>();
        services.AddSingleton<IPrescriptionImageStore>(new EncryptedFilePrescriptionImageStore(prescriptionImages));
    }

    #endregion
}
