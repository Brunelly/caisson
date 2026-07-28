using Caisson.Domain.Discovery;
using Caisson.Orchestration.Scheduling;
using Caisson.Orchestration.Tests.Fakes;
using FluentAssertions;
using Xunit;

namespace Caisson.Orchestration.Tests;

/// <summary>DB-free tests for the scheduler's fixed-interval-plus-jitter advancement (story #8, AC3).</summary>
public sealed class DiscoverySchedulerTests
{
    private static readonly DateTime Now = new(2026, 7, 28, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Next_run_is_interval_plus_deterministic_jitter()
    {
        var schedule = new RackDiscoverySchedule(Guid.NewGuid(), enabled: true, intervalSeconds: 900, jitterSeconds: 60);

        var next = DiscoveryScheduler.ComputeNextRun(Now, schedule, new FixedJitterSource(30));

        next.Should().Be(Now.AddSeconds(900 + 30));
    }

    [Fact]
    public void Next_run_with_zero_jitter_is_exactly_the_interval()
    {
        var schedule = new RackDiscoverySchedule(Guid.NewGuid(), enabled: true, intervalSeconds: 300, jitterSeconds: 0);

        var next = DiscoveryScheduler.ComputeNextRun(Now, schedule, new FixedJitterSource(0));

        next.Should().Be(Now.AddSeconds(300));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(120)]
    public void Random_jitter_stays_within_bounds(int maxJitter)
    {
        var source = new RandomJitterSource();

        for (var i = 0; i < 1000; i++)
        {
            source.NextJitterSeconds(maxJitter).Should().BeInRange(0, maxJitter);
        }
    }
}
