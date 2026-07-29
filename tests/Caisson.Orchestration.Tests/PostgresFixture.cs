using Caisson.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Caisson.Orchestration.Tests;

/// <summary>
/// Provisions an ephemeral PostgreSQL database for a test class — copied from
/// <c>Caisson.Infrastructure.Tests.PostgresFixture</c> (same shape) so <c>DriftSchedulerTests</c> can
/// exercise <c>DriftScheduler.TickAsync</c> (which uses <c>LatestVersionPerRackAsync</c>'s
/// <c>FromSqlRaw</c>) against a real database. It <b>prefers</b> the <c>CAISSON_TEST_DB</c> environment
/// variable (a CI service container or a locally-installed Postgres in this Docker-less sandbox) and
/// <b>falls back</b> to a <see cref="PostgreSqlContainer"/> when the variable is absent. Each fixture
/// instance creates its own uniquely-named database so test classes are fully isolated and can freely
/// migrate up and down.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>Environment variable that, when set, points the suite at an existing Postgres.</summary>
    public const string TestDbEnvVar = "CAISSON_TEST_DB";

    private readonly string _databaseName = "caisson_orch_it_" + Guid.NewGuid().ToString("N");
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

            await using (var terminate = new NpgsqlCommand(
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid();",
                connection))
            {
                terminate.Parameters.AddWithValue("db", _databaseName);
                await terminate.ExecuteNonQueryAsync();
            }

            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\";", connection);
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
}
