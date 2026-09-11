using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Devage.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public const string ConnectionStringEnvVar = "DEVAGE_CONNECTION_STRING";
    public const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=devage;Username=devage;Password=devage";

    public static IServiceCollection AddDevagePersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringEnvVar)
            ?? configuration.GetConnectionString("Devage")
            ?? DefaultConnectionString;

        services.AddDbContext<DevageDbContext>(options =>
            options.UseNpgsql(connectionString));

        return services;
    }

    public static async Task MigrateDevageDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevageDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }
}
