using Caisson.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Caisson.VirtualRack.IntegrationTests;

/// <summary>
/// Provisions an ephemeral, isolated PostgreSQL database for the API integration tests. Mirrors the
/// Infrastructure test fixture: prefers the <c>CAISSON_TEST_DB</c> environment variable (CI service
/// container or a locally-installed Postgres) and falls back to Testcontainers when it is absent, so
/// the suite gates cleanly when Docker/Postgres is unavailable.
/// </summary>
public sealed class PostgresHarness : IAsyncDisposable
{
    private const string TestDbEnvVar = "CAISSON_TEST_DB";

    private readonly string _databaseName = "caisson_vrack_it_" + Guid.NewGuid().ToString("N");
    private PostgreSqlContainer? _container;
    private string _adminConnectionString = string.Empty;

    /// <summary>Whether an ephemeral Postgres could be provisioned (false → the suite should skip).</summary>
    public bool Available { get; private set; }

    /// <summary>Connection string pointing at this harness's isolated database.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(TestDbEnvVar);
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            try
            {
                _container = new PostgreSqlBuilder().WithImage("postgres:16").Build();
                await _container.StartAsync();
                baseConnectionString = _container.GetConnectionString();
            }
            catch (Exception)
            {
                // No Docker and no CAISSON_TEST_DB → the DB-backed API suite cannot run here.
                Available = false;
                return;
            }
        }

        var admin = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = "postgres" };
        _adminConnectionString = admin.ConnectionString;

        var target = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = _databaseName };
        ConnectionString = target.ConnectionString;

        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\";", connection);
        await create.ExecuteNonQueryAsync();

        await MigrateAsync();
        Available = true;
    }

    /// <summary>Builds a fresh context bound to this harness's isolated database.</summary>
    public CaissonDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CaissonDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new CaissonDbContext(options);
    }

    private async Task MigrateAsync()
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (!string.IsNullOrEmpty(_adminConnectionString))
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
                // Best-effort cleanup.
            }
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
