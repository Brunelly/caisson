using Caisson.Ingestion.Git.GitHub;
using Caisson.Ingestion.Options;
using Caisson.Ingestion.Scheduling;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Caisson.Ingestion.Tests.Scheduling;

/// <summary>
/// DB-free tests for the PR status poller (story #173, Task #211b): <c>TickAsync</c> is deterministically
/// drivable and mints one fresh correlation id per tick; a throwing sync service is isolated so the host is
/// never crashed; and disabled configuration never starts polling.
/// </summary>
public sealed class GitPullRequestStatusPollerTests
{
    [Fact]
    public async Task TickAsync_invokes_sync_with_a_fresh_correlation_id()
    {
        var fake = new FakeSyncService();
        var poller = CreatePoller(fake, enabled: true);

        await poller.TickAsync(default);
        await poller.TickAsync(default);

        fake.Correlations.Should().HaveCount(2);
        fake.Correlations.Should().OnlyHaveUniqueItems();
        fake.Correlations.Should().NotContain(Guid.Empty);
    }

    [Fact]
    public async Task Disabled_configuration_never_starts_polling()
    {
        var fake = new FakeSyncService();
        var poller = CreatePoller(fake, enabled: false);

        await poller.StartAsync(default);
        await poller.StopAsync(default);

        fake.Correlations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_throwing_sync_service_propagates_from_tick_but_is_isolated_by_execute()
    {
        // TickAsync surfaces the fault (so tests see it) ...
        var throwing = new FakeSyncService { Throw = true };
        var poller = CreatePoller(throwing, enabled: true);

        var act = async () => await poller.TickAsync(default);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // ... while ExecuteAsync's per-tick try/catch keeps the host alive (StartAsync/StopAsync never throw).
        await poller.StartAsync(default);
        await Task.Delay(50);
        await poller.StopAsync(default);
    }

    private static GitPullRequestStatusPoller CreatePoller(FakeSyncService fake, bool enabled)
    {
        var services = new ServiceCollection();
        services.AddScoped<IGitPullRequestStatusSyncService>(_ => fake);
        var provider = services.BuildServiceProvider();

        return new GitPullRequestStatusPoller(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            Microsoft.Extensions.Options.Options.Create(new GitPullRequestStatusOptions { Enabled = enabled, PollIntervalSeconds = 60 }),
            NullLogger<GitPullRequestStatusPoller>.Instance);
    }

    private sealed class FakeSyncService : IGitPullRequestStatusSyncService
    {
        public List<Guid> Correlations { get; } = new();

        public bool Throw { get; set; }

        public Task<int> SyncDueAsync(Guid correlationId, CancellationToken cancellationToken)
        {
            Correlations.Add(correlationId);
            if (Throw)
            {
                throw new InvalidOperationException("boom");
            }

            return Task.FromResult(0);
        }
    }
}
