using Microsoft.Extensions.Configuration;

namespace Caisson.Infrastructure.LiveUpdates;

/// <summary>
/// Configuration for the live-updates pipeline (story #9, ADR 0014), bound from the <c>Realtime</c>
/// section. The Redis connection string is deliberately NOT bound here: it is resolved from the
/// <c>CAISSON_REDIS</c> environment variable / Key Vault first (mirroring the <c>CAISSON_DB</c> pattern),
/// never from appsettings/source, so no secret lives in the repo.
/// </summary>
public sealed class RealtimeOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Realtime";

    /// <summary>The environment variable / Key Vault key holding the Redis connection string.</summary>
    public const string RedisConnectionStringEnvVar = "CAISSON_REDIS";

    /// <summary>Whether the live-updates feature is enabled (SignalR hub + Redis relay). Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The single Redis pub/sub channel events are published to.</summary>
    public string EventsChannel { get; set; } = TopologyEventChannels.Default;

    /// <summary>The SignalR Redis backplane channel prefix (isolates the backplane from the app channel).</summary>
    public string SignalRChannelPrefix { get; set; } = "caisson-signalr";

    /// <summary>Seconds between server heartbeats (story #9, Q2). Default 10.</summary>
    public int HeartbeatSeconds { get; set; } = 10;

    /// <summary>
    /// Resolves the Redis connection string from <c>CAISSON_REDIS</c> (env/Key Vault) first, then the
    /// <c>ConnectionStrings:Redis</c> configuration entry — mirroring the <c>CAISSON_DB</c> resolution in
    /// the API host. Returns null when neither is set (→ live updates run in no-op/single-instance mode).
    /// </summary>
    public static string? ResolveRedisConnectionString(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var fromEnv = Environment.GetEnvironmentVariable(RedisConnectionStringEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        var fromConfig = configuration.GetConnectionString("Redis");
        return string.IsNullOrWhiteSpace(fromConfig) ? null : fromConfig;
    }
}
