using Caisson.Infrastructure.LiveUpdates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Caisson.Infrastructure.DependencyInjection;

/// <summary>
/// DI registration for the dependency-free live-updates seam (story #9, ADR 0014): the event publisher,
/// the seq allocator, the shared metrics, and — when a Redis connection string is present — a resilient
/// singleton <see cref="IConnectionMultiplexer"/>. Mirrors <c>AddCaissonPersistence</c>/<c>AddCaissonOrchestration</c>.
/// This is the layer both <c>Caisson.Orchestration</c> and <c>Caisson.Api</c> can call; the SignalR hub
/// and Redis backplane wiring live in <c>Caisson.Api</c> (<c>AddCaissonRealtime</c>).
/// </summary>
public static class LiveUpdatesServiceCollectionExtensions
{
    /// <summary>
    /// Registers the live-updates publisher/sequencer/metrics. When live updates are enabled AND a Redis
    /// connection string is resolvable (env <c>CAISSON_REDIS</c> / <c>ConnectionStrings:Redis</c>), wires
    /// the Redis publisher over a lazy, resilient multiplexer (<c>AbortOnConnectFail=false</c> so a
    /// late/absent Redis never crashes startup); otherwise falls back to the no-op publisher and the
    /// in-process sequencer so the DB pipeline never hard-depends on Redis.
    /// </summary>
    public static IServiceCollection AddCaissonLiveUpdates(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RealtimeOptions>().Bind(configuration.GetSection(RealtimeOptions.SectionName));
        services.TryAddSingleton<TopologyMetrics>();

        var useRedis = RealtimeOptions.IsRedisEnabled(configuration, out var redisConnectionString);

        // The publisher/sequencer are singletons; decide them here rather than TryAdd so this call wins
        // over the no-op defaults registered by AddCaissonPersistence, regardless of registration order.
        services.RemoveAll<ITopologyEventPublisher>();
        services.RemoveAll<ITopologyEventSequencer>();

        if (useRedis)
        {
            services.TryAddSingleton<IConnectionMultiplexer>(_ =>
            {
                var config = ConfigurationOptions.Parse(redisConnectionString!);
                // A late/absent Redis must not crash startup; the multiplexer reconnects in the background.
                config.AbortOnConnectFail = false;
                return ConnectionMultiplexer.Connect(config);
            });
            services.AddSingleton<ITopologyEventPublisher, RedisTopologyEventPublisher>();
            services.AddSingleton<ITopologyEventSequencer, RedisTopologyEventSequencer>();
        }
        else
        {
            services.AddSingleton<ITopologyEventPublisher, NoOpTopologyEventPublisher>();
            services.AddSingleton<ITopologyEventSequencer, InProcessTopologyEventSequencer>();
        }

        return services;
    }
}
