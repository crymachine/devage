using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Devage.Persistence;

public sealed class DevageDbContextFactory : IDesignTimeDbContextFactory<DevageDbContext>
{
    public DevageDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable(PersistenceServiceCollectionExtensions.ConnectionStringEnvVar)
            ?? PersistenceServiceCollectionExtensions.DefaultConnectionString;

        var options = new DbContextOptionsBuilder<DevageDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new DevageDbContext(options);
    }
}
