using Caisson.Infrastructure.Persistence.Drift;
using Caisson.Orchestration.Drift;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caisson.Orchestration.Tests;

/// <summary>
/// DB-free tests of <see cref="DriftRecomputeSignal"/>'s enqueue/drain shape and
/// <see cref="DriftRecomputeRunner.ProcessOneAsync"/>'s exception isolation (story #64, AC4) — a fake
/// <see cref="IDriftComputationService"/> stands in for the real DB-touching one.
/// </summary>
public sealed class DriftRecomputeRunnerTests
{
    [Fact]
    public void Signal_drains_only_the_enqueued_racks_in_order()
    {
        var signal = new DriftRecomputeSignal();
        var rackA = Guid.NewGuid();
        var rackB = Guid.NewGuid();

        signal.Enqueue(rackA);
        signal.Enqueue(rackB);

        signal.Reader.TryRead(out var first).Should().BeTrue();
        signal.Reader.TryRead(out var second).Should().BeTrue();
        signal.Reader.TryRead(out _).Should().BeFalse();

        first.Should().Be(rackA);
        second.Should().Be(rackB);
    }

    [Fact]
    public async Task ProcessOneAsync_computes_drift_for_the_enqueued_rack()
    {
        var fake = new FakeDriftComputationService();
        var runner = CreateRunner(fake);
        var rackId = Guid.NewGuid();

        await runner.ProcessOneAsync(rackId, default);

        fake.ComputedRackIds.Should().ContainSingle().Which.Should().Be(rackId);
    }

    [Fact]
    public async Task ProcessOneAsync_swallows_a_computation_failure_without_throwing()
    {
        var fake = new FakeDriftComputationService { ThrowOnNextCall = new InvalidOperationException("boom") };
        var runner = CreateRunner(fake);

        var act = async () => await runner.ProcessOneAsync(Guid.NewGuid(), default);

        await act.Should().NotThrowAsync();
    }

    private static DriftRecomputeRunner CreateRunner(IDriftComputationService service)
    {
        var services = new ServiceCollection();
        services.AddSingleton(service);
        var provider = services.BuildServiceProvider();

        var signal = new DriftRecomputeSignal();
        return new DriftRecomputeRunner(
            provider.GetRequiredService<IServiceScopeFactory>(), signal, NullLogger<DriftRecomputeRunner>.Instance);
    }

    private sealed class FakeDriftComputationService : IDriftComputationService
    {
        public List<Guid> ComputedRackIds { get; } = new();

        public Exception? ThrowOnNextCall { get; set; }

        public Task ComputeAndPersistAsync(Guid rackId, Guid correlationId, CancellationToken cancellationToken = default)
        {
            if (ThrowOnNextCall is { } ex)
            {
                ThrowOnNextCall = null;
                throw ex;
            }

            ComputedRackIds.Add(rackId);
            return Task.CompletedTask;
        }
    }
}
