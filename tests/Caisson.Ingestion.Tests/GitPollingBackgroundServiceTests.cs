using Caisson.Domain.DesiredState;
using Caisson.Ingestion.Ingestion;
using Caisson.Ingestion.Options;
using Caisson.Ingestion.Scheduling;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caisson.Ingestion.Tests;

/// <summary>DB-free tests for the poll trigger (story #62, AC1): it must call the SAME shared entry point the webhook uses.</summary>
public sealed class GitPollingBackgroundServiceTests
{
    [Fact]
    public async Task TickAsync_invokes_the_shared_run_entry_point_with_poll_trigger_and_no_delivery_id()
    {
        var fake = new FakeDesiredStateIngestionService();
        var service = CreateService(fake, enabled: true);

        await service.TickAsync(default);

        fake.Calls.Should().ContainSingle();
        fake.Calls[0].Trigger.Should().Be(IngestionTriggerType.Poll);
        fake.Calls[0].WebhookDeliveryId.Should().BeNull();
    }

    [Fact]
    public async Task Disabled_configuration_never_starts_polling()
    {
        var fake = new FakeDesiredStateIngestionService();
        var service = CreateService(fake, enabled: false);

        // ExecuteAsync (via StartAsync) must return immediately without ever calling TickAsync/RunAsync.
        await service.StartAsync(default);
        await service.StopAsync(default);

        fake.Calls.Should().BeEmpty();
    }

    private static GitPollingBackgroundService CreateService(FakeDesiredStateIngestionService fake, bool enabled)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDesiredStateIngestionService>(fake);
        var provider = services.BuildServiceProvider();

        return new GitPollingBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            Microsoft.Extensions.Options.Options.Create(new GitIngestionOptions { Enabled = enabled, PollIntervalSeconds = 1 }),
            NullLogger<GitPollingBackgroundService>.Instance);
    }
}
