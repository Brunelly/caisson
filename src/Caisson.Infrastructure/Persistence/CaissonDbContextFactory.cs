using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Caisson.Infrastructure.Persistence;

/// <summary>
/// Design-time factory used by the EF Core tools (<c>dotnet ef migrations add</c>,
/// <c>dotnet ef database update</c>) so migrations can be generated and applied without an API host.
/// The connection string is read from the <c>CAISSON_DB</c> environment variable; a localhost
/// development default is used when it is unset. <b>No secrets are hard-coded.</b>
/// </summary>
public sealed class CaissonDbContextFactory : IDesignTimeDbContextFactory<CaissonDbContext>
{
    /// <summary>Environment variable holding the PostgreSQL connection string.</summary>
    public const string ConnectionStringEnvVar = "CAISSON_DB";

    private const string LocalDevelopmentDefault =
        "Host=localhost;Port=5432;Database=caisson;Username=caisson;Password=caisson";

    /// <inheritdoc />
    public CaissonDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringEnvVar) ?? LocalDevelopmentDefault;

        var options = new DbContextOptionsBuilder<CaissonDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(
                typeof(CaissonDbContextFactory).Assembly.GetName().Name))
            .Options;

        return new CaissonDbContext(options);
    }
}
