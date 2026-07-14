using CampCost.Core.Interfaces;
using CampCost.Core.Services;
using CampCost.Infrastructure.Data;
using CampCost.Infrastructure.Services;
using Going.Plaid;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CampCost.Infrastructure;

/// <summary>
/// Single call from Program.cs wires up the entire Infrastructure layer.
/// The Api project never needs to reference Going.Plaid or EF Core directly.
/// </summary>
public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration config)
    {
        // Database — use NpgsqlDataSourceBuilder so we can register the expense_category
        // PostgreSQL enum. Without this, Npgsql sends 'text' and Postgres rejects it
        // with "column is of type expense_category but expression is of type text".
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(
            config.GetConnectionString("DefaultConnection"));
        dataSourceBuilder.MapEnum<ExpenseCategoryEnum>("expense_category");
        var dataSource = dataSourceBuilder.Build();

        // Encrypts secrets (Plaid access tokens) at rest. Singleton because the EF model caches
        // the value-converter expression that captures this instance; the key comes from
        // Encryption:TokenKey (env/user-secrets), never the database. Registered before the
        // DbContext so DI can inject it into the 2-arg CampCostDbContext constructor.
        services.AddSingleton<ITokenProtector, AesGcmTokenProtector>();

        services.AddDbContext<CampCostDbContext>(options =>
            options.UseNpgsql(dataSource));

        // Plaid client — uses Going.Plaid's built-in DI extension
        // Reads ClientId, Secret, Environment from config section "Plaid"
        services.AddHttpClient();
        services.Configure<PlaidOptions>(config.GetSection("Plaid"));
        services.AddSingleton<PlaidClient>();

        // Core stateless services
        services.AddSingleton<TransactionCategorizationService>();
        services.AddSingleton<TransactionMatchingService>();
        services.AddSingleton<BudgetCalculationService>();

        // Webhook signature verification — singleton so the Plaid verification-key
        // cache survives across requests.
        services.AddSingleton<IPlaidWebhookVerifier, PlaidWebhookVerifier>();

        // Scoped services (one per HTTP request)
        services.AddScoped<IPlanService, PlanService>();
        services.AddScoped<IPlaidService, PlaidService>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<ITransactionSyncService, TransactionSyncService>();
        services.AddScoped<ITransactionAssignmentService, TransactionAssignmentService>();

        return services;
    }
}
