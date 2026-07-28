using Caisson.Api.Realtime.Hubs;
using Caisson.Infrastructure.LiveUpdates;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// SignalR hub auth/subscription/authorization tests (story #9, AC3). Auth is the header-driven
/// <see cref="TestAuthHandler"/>; the client runs against the in-memory <c>TestServer</c> via
/// <c>HttpMessageHandlerFactory</c>. Connect/reject cases gate on Postgres; "receives an event" cases
/// additionally gate on Redis, since live-updates delivery routes through Redis pub/sub.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TopologyHubTests
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(15);

    private readonly CaissonApiFactory _factory;

    public TopologyHubTests(CaissonApiFactory factory) => _factory = factory;

    [SkippableFact]
    public async Task Anonymous_connection_is_rejected()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres; skipped when unavailable.");

        await using var connection = BuildConnection(user: null, roles: null);
        var connect = async () => await connection.StartAsync();
        await connect.Should().ThrowAsync<Exception>();
    }

    [SkippableFact]
    public async Task Authenticated_without_a_recognised_role_is_rejected()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres; skipped when unavailable.");

        await using var connection = BuildConnection("tester", "SomeUnrecognisedRole");
        var connect = async () => await connection.StartAsync();
        await connect.Should().ThrowAsync<Exception>();
    }

    [SkippableTheory]
    [InlineData("Admin")]
    [InlineData("Operator")]
    [InlineData("ReadOnly")]
    [InlineData("ServiceAccount")]
    public async Task Each_read_role_connects_subscribes_and_receives_a_group_event(string role)
    {
        Skip.IfNot(_factory.Available && _factory.RedisAvailable, "Requires Postgres and Redis; skipped when unavailable.");

        await using var connection = BuildConnection("tester", role);
        var received = new TaskCompletionSource<SnapshotUpdatedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<SnapshotUpdatedEvent>(nameof(ITopologyClient.SnapshotUpdated), e => received.TrySetResult(e));

        await connection.StartAsync();
        await connection.InvokeAsync("SubscribeToRack", _factory.Seed.RackId);
        await PublishSnapshotAsync(_factory.Seed.RackId);

        var completed = await Task.WhenAny(received.Task, Task.Delay(ReceiveTimeout));
        completed.Should().Be(received.Task, "the subscribed client should receive the group event within the budget");
        (await received.Task).RackId.Should().Be(_factory.Seed.RackId);
    }

    [SkippableFact]
    public async Task Subscribe_to_a_nonexistent_rack_throws_a_hub_exception()
    {
        Skip.IfNot(_factory.Available, "Requires Postgres; skipped when unavailable.");

        await using var connection = BuildConnection("tester", "Admin");
        await connection.StartAsync();

        var subscribe = async () => await connection.InvokeAsync("SubscribeToRack", Guid.NewGuid());
        (await subscribe.Should().ThrowAsync<HubException>()).Which.Message.Should().Contain("does not exist");
    }

    [SkippableFact]
    public async Task A_subscriber_to_one_rack_does_not_receive_another_racks_event()
    {
        Skip.IfNot(_factory.Available && _factory.RedisAvailable, "Requires Postgres and Redis; skipped when unavailable.");

        await using var connection = BuildConnection("tester", "ReadOnly");
        var received = new TaskCompletionSource<SnapshotUpdatedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<SnapshotUpdatedEvent>(nameof(ITopologyClient.SnapshotUpdated), e => received.TrySetResult(e));

        await connection.StartAsync();
        await connection.InvokeAsync("SubscribeToRack", _factory.Seed.RackId);

        // Publish for a DIFFERENT rack; the rack:A subscriber must not receive it.
        await PublishSnapshotAsync(Guid.NewGuid());

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        completed.Should().NotBe(received.Task, "rack-scoped groups must isolate events to the subscribed rack");
    }

    private Task PublishSnapshotAsync(Guid rackId)
        => _factory.Services.GetRequiredService<ITopologyEventPublisher>().PublishSnapshotUpdatedAsync(
            new SnapshotUpdatedEvent(
                rackId, JobId: null, Guid.NewGuid(), 1, new SnapshotSummary(1, 1, 1, 1, 0, 0),
                DateTimeOffset.UtcNow, 1, Guid.NewGuid()));

    private HubConnection BuildConnection(string? user, string? roles)
    {
        var builder = new HubConnectionBuilder().WithUrl(
            new Uri(_factory.Server.BaseAddress, "hubs/topology"),
            options =>
            {
                // Route the client through the in-memory TestServer; LongPolling is what TestServer supports.
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                if (user is not null)
                {
                    options.Headers.Add(TestAuthHandler.UserHeader, user);
                }

                if (roles is not null)
                {
                    options.Headers.Add(TestAuthHandler.RolesHeader, roles);
                }
            });

        return builder.Build();
    }
}
