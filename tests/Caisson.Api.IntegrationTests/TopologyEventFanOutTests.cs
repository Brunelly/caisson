using Caisson.Api.Realtime.Hubs;
using Caisson.Domain.Topology;
using Caisson.Infrastructure.LiveUpdates;
using Caisson.Infrastructure.Persistence;
using Caisson.Orchestration.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;
using FluentAssertions;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// The multi-instance AC (story #9, the AC that matters most): two API hosts share ONE Redis (backplane +
/// pub/sub channel) and ONE Postgres. A client connected to host B subscribes to a rack; an event produced
/// on host A must reach that client EXACTLY once via the Redis backplane + exactly-once relay guard.
/// Mirrors the harness conventions — gated by <c>Skip.IfNot</c> so it skips cleanly when Redis/Docker are
/// absent, while the existing Postgres-only suite is unaffected.
/// </summary>
public sealed class TopologyEventFanOutTests : IAsyncLifetime
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    private readonly PostgresHarness _postgres = new();
    private readonly RedisHarness _redis = new();

    public async Task InitializeAsync()
    {
        await _postgres.InitializeAsync();
        await _redis.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _redis.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task An_event_produced_on_host_A_reaches_a_client_on_host_B_exactly_once()
    {
        Skip.IfNot(_postgres.Available && _redis.Available, "Requires Postgres and Redis; skipped when unavailable.");

        var rackId = await SeedRackAsync();
        await using var hostA = new SharedHost(_postgres.ConnectionString, _redis.ConnectionString);
        await using var hostB = new SharedHost(_postgres.ConnectionString, _redis.ConnectionString);
        // Force both hosts to build (and thus start the relay subscribers) before producing an event.
        _ = hostA.Services;
        _ = hostB.Services;

        await using var connection = BuildConnection(hostB, "tester", "Operator");
        var received = new List<SnapshotUpdatedEvent>();
        var first = new TaskCompletionSource<SnapshotUpdatedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<SnapshotUpdatedEvent>(nameof(ITopologyClient.SnapshotUpdated), e =>
        {
            lock (received)
            {
                received.Add(e);
            }

            first.TrySetResult(e);
        });

        await connection.StartAsync();
        await connection.InvokeAsync("SubscribeToRack", rackId);

        // Produce the SAME event (same EventId) twice on host A — the exactly-once guard must de-dup.
        var @event = new SnapshotUpdatedEvent(
            rackId, JobId: null, Guid.NewGuid(), 1, new SnapshotSummary(2, 20, 3, 4, 0, 1),
            DateTimeOffset.UtcNow, 1, Guid.NewGuid());
        var publisher = hostA.Services.GetRequiredService<ITopologyEventPublisher>();
        await publisher.PublishSnapshotUpdatedAsync(@event);
        await publisher.PublishSnapshotUpdatedAsync(@event);

        var completed = await Task.WhenAny(first.Task, Task.Delay(Budget));
        completed.Should().Be(first.Task, "the cross-instance event should reach host B's client within the 2s budget");
        (await first.Task).RackId.Should().Be(rackId);

        // Give any (wrongly) duplicated relay a chance to arrive, then assert exactly-once.
        await Task.Delay(TimeSpan.FromSeconds(1));
        lock (received)
        {
            received.Should().ContainSingle(e => e.EventId == @event.EventId);
        }
    }

    [SkippableFact]
    public async Task An_unsigned_message_published_directly_to_the_channel_is_never_relayed()
    {
        // Finding #2: the relay only trusts messages carrying a valid HMAC tag. A message published
        // straight to the Redis channel — bypassing RedisTopologyEventPublisher entirely, as a
        // misconfigured ACL or a misrouted publisher on the shared instance would — must never reach a
        // client, even though it decodes to a perfectly well-formed event.
        Skip.IfNot(_postgres.Available && _redis.Available, "Requires Postgres and Redis; skipped when unavailable.");

        var rackId = await SeedRackAsync();
        await using var host = new SharedHost(_postgres.ConnectionString, _redis.ConnectionString);
        _ = host.Services;

        await using var connection = BuildConnection(host, "tester", "Operator");
        var received = new TaskCompletionSource<SnapshotUpdatedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<SnapshotUpdatedEvent>(nameof(ITopologyClient.SnapshotUpdated), e => received.TrySetResult(e));

        await connection.StartAsync();
        await connection.InvokeAsync("SubscribeToRack", rackId);

        var @event = new SnapshotUpdatedEvent(
            rackId, JobId: null, Guid.NewGuid(), 1, new SnapshotSummary(2, 20, 3, 4, 0, 1),
            DateTimeOffset.UtcNow, 1, Guid.NewGuid());
        var unsignedJson = TopologyEventSerialization.Serialize(@event);
        var multiplexer = host.Services.GetRequiredService<IConnectionMultiplexer>();
        await multiplexer.GetSubscriber().PublishAsync(RedisChannel.Literal(TopologyEventChannels.Default), unsignedJson);

        var completed = await Task.WhenAny(received.Task, Task.Delay(Budget));
        completed.Should().NotBe(received.Task, "an unsigned message must never be relayed to a client");
    }

    private async Task<Guid> SeedRackAsync()
    {
        var rackId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<CaissonDbContext>().UseNpgsql(_postgres.ConnectionString).Options;
        await using var context = new CaissonDbContext(options);
        context.Racks.Add(new Rack(rackId, "rack-" + rackId.ToString("N"), "Fan-out Rack", DateTime.UtcNow));
        await context.SaveChangesAsync();
        return rackId;
    }

    private static HubConnection BuildConnection(WebApplicationFactory<Program> host, string user, string roles)
        => new HubConnectionBuilder()
            .WithUrl(
                new Uri(host.Server.BaseAddress, "hubs/topology"),
                options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => host.Server.CreateHandler();
                    options.Headers.Add(TestAuthHandler.UserHeader, user);
                    options.Headers.Add(TestAuthHandler.RolesHeader, roles);
                })
            .Build();

    /// <summary>An API host bound to an externally-supplied shared Postgres + Redis.</summary>
    private sealed class SharedHost : WebApplicationFactory<Program>
    {
        private readonly string _postgresConnectionString;
        private readonly string _redisConnectionString;

        public SharedHost(string postgresConnectionString, string redisConnectionString)
        {
            _postgresConnectionString = postgresConnectionString;
            _redisConnectionString = redisConnectionString;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Caisson", _postgresConnectionString);
            builder.UseSetting("ConnectionStrings:Redis", _redisConnectionString);
            builder.UseSetting("Realtime:Enabled", "true");
            builder.UseSetting("Realtime:HeartbeatSeconds", "30");

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll(typeof(DbContextOptions<CaissonDbContext>));
                services.RemoveAll(typeof(DbContextOptions));
                services.AddDbContext<CaissonDbContext>(o => o.UseNpgsql(_postgresConnectionString));

                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

                // No discovery activity is needed for the fan-out proof; keep the hosts quiet.
                services.Configure<DiscoveryOrchestrationOptions>(o =>
                {
                    o.RunnerEnabled = false;
                    o.SchedulerEnabled = false;
                });
            });
        }
    }
}
