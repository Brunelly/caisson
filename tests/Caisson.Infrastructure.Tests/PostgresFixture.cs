using Caisson.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Provisions an ephemeral PostgreSQL database for a test class. It <b>prefers</b> the
/// <c>CAISSON_TEST_DB</c> environment variable (a CI service container or a locally-installed Postgres
/// in this Docker-less sandbox) and <b>falls back</b> to a <see cref="PostgreSqlContainer"/> when the
/// variable is absent — keeping the suite green in both CI and the sandbox. Each fixture instance
/// creates its own uniquely-named database so test classes are fully isolated and can freely migrate
/// up and down.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>Environment variable that, when set, points the suite at an existing Postgres.</summary>
    public const string TestDbEnvVar = "CAISSON_TEST_DB";

    private readonly string _databaseName = "caisson_it_" + Guid.NewGuid().ToString("N");
    private PostgreSqlContainer? _container;
    private string _adminConnectionString = string.Empty;

    /// <summary>Connection string pointing at this fixture's isolated database.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(TestDbEnvVar);
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .Build();
            await _container.StartAsync();
            baseConnectionString = _container.GetConnectionString();
        }

        // Derive an admin connection (maintenance database) and this fixture's isolated database.
        var admin = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = "postgres" };
        _adminConnectionString = admin.ConnectionString;

        var target = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = _databaseName };
        ConnectionString = target.ConnectionString;

        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\";", connection);
        await create.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        try
        {
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync();

            // Terminate any lingering sessions, then drop the isolated database.
            await using (var terminate = new NpgsqlCommand(
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid();",
                connection))
            {
                terminate.Parameters.AddWithValue("db", _databaseName);
                await terminate.ExecuteNonQueryAsync();
            }

            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{_databaseName}\";", connection);
            await drop.ExecuteNonQueryAsync();
        }
        catch (NpgsqlException)
        {
            // Best-effort cleanup — a failed drop must not fail the test run.
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Builds a fresh context bound to this fixture's isolated database.</summary>
    public CaissonDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CaissonDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new CaissonDbContext(options);
    }

    /// <summary>Applies all migrations to the isolated database.</summary>
    public async Task MigrateAsync()
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    /// <summary>Returns the set of base table names in the public schema.</summary>
    public async Task<HashSet<string>> GetTableNamesAsync()
        => await QuerySingleColumnAsync(
            "SELECT table_name FROM information_schema.tables " +
            "WHERE table_schema = 'public' AND table_type = 'BASE TABLE';");

    /// <summary>Returns the set of index names in the public schema.</summary>
    public async Task<HashSet<string>> GetIndexNamesAsync()
        => await QuerySingleColumnAsync(
            "SELECT indexname FROM pg_indexes WHERE schemaname = 'public';");

    private async Task<HashSet<string>> QuerySingleColumnAsync(string sql)
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }
}
