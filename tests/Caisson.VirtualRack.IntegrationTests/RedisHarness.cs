using Testcontainers.Redis;

namespace Caisson.VirtualRack.IntegrationTests;

/// <summary>
/// Provisions an ephemeral Redis for the live-updates tests (story #9). An exact structural mirror of
/// <see cref="PostgresHarness"/>: it prefers the <c>CAISSON_TEST_REDIS</c> environment variable (a CI
/// service container or a local Redis) and falls back to Testcontainers when it is absent, so the
/// Redis-gated cases gate cleanly (Skipped, not failed) when Docker/Redis is unavailable. It is an
/// independent flag from Postgres so the existing Postgres-only suite keeps running when Redis is absent.
/// </summary>
public sealed class RedisHarness : IAsyncDisposable
{
    private const string TestRedisEnvVar = "CAISSON_TEST_REDIS";

    private RedisContainer? _container;

    /// <summary>Whether an ephemeral Redis could be provisioned (false → Redis-gated cases skip).</summary>
    public bool Available { get; private set; }

    /// <summary>StackExchange.Redis connection string for this harness's Redis.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(TestRedisEnvVar);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            try
            {
                _container = new RedisBuilder().WithImage("redis:7").Build();
                await _container.StartAsync();
                connectionString = _container.GetConnectionString();
            }
            catch (Exception)
            {
                // No Docker and no CAISSON_TEST_REDIS → the Redis-gated cases cannot run here.
                Available = false;
                return;
            }
        }

        ConnectionString = connectionString;
        Available = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
