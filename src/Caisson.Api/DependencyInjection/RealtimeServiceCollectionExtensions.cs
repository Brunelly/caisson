using Caisson.Api.Realtime;
using Caisson.Api.Realtime.Hubs;
using Caisson.Infrastructure.DependencyInjection;
using Caisson.Infrastructure.LiveUpdates;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Caisson.Api.DependencyInjection;

/// <summary>
/// Wires the live-updates delivery side in the API host (story #9, ADR 0014): the dependency-free seam
/// (<see cref="LiveUpdatesServiceCollectionExtensions.AddCaissonLiveUpdates"/>), the SignalR hub with a
/// Redis backplane, the per-instance relay subscriber + heartbeat, and the Redis health check. Live
/// delivery REQUIRES Redis: with no connection string the publisher is the no-op implementation and
/// neither the relay subscriber nor the heartbeat is registered, so the hub still accepts connections
/// but delivers no events and emits no heartbeats — an intentional dev/CI fallback, not a delivery mode.
/// The real deployment target is multiple instances + Redis.
/// </summary>
public static class RealtimeServiceCollectionExtensions
{
    /// <summary>Registers the SignalR hub, Redis backplane/relay, metrics and health for live updates.</summary>
    public static IServiceCollection AddCaissonRealtime(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // The dependency-free seam: publisher + sequencer + multiplexer + metrics.
        services.AddCaissonLiveUpdates(configuration);

        var options = configuration.GetSection(RealtimeOptions.SectionName).Get<RealtimeOptions>() ?? new RealtimeOptions();
        var useRedis = RealtimeOptions.IsRedisEnabled(configuration, out var redisConnectionString);

        services.TryAddSingleton<TopologyHubLoggingFilter>();
        var signalr = services.AddSignalR(o => o.AddFilter<TopologyHubLoggingFilter>());

        if (useRedis)
        {
            // Redis backplane: SignalR broadcasts fan out across all instances (story requirement).
            signalr.AddStackExchangeRedis(redisConnectionString!, o =>
                o.Configuration.ChannelPrefix = RedisChannel.Literal(options.SignalRChannelPrefix));

            // Per-instance relay from the pub/sub channel to hub groups + the server heartbeat.
            services.AddHostedService<RedisTopologyEventSubscriber>();
            services.AddHostedService<TopologyHeartbeatService>();

            // Redis connectivity health (mirrors the conditional Npgsql check), tagged for /health/ready.
            services.AddHealthChecks().AddRedis(redisConnectionString!, name: "redis", tags: new[] { "ready" });
        }

        return services;
    }
}
