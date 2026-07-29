using System.Collections.Concurrent;
using Caisson.Infrastructure.LiveUpdates;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// A recording <see cref="ITopologyEventPublisher"/> — the same style as the existing fakes — that
/// captures every published event so a test can assert on it (story #9). It never throws.
/// </summary>
public sealed class RecordingTopologyEventPublisher : ITopologyEventPublisher
{
    private readonly ConcurrentQueue<SnapshotUpdatedEvent> _snapshots = new();
    private readonly ConcurrentQueue<DiscoveryJobStatusChangedEvent> _statuses = new();
    private readonly ConcurrentQueue<DriftApplyJobStatusChangedEvent> _driftApplyStatuses = new();
    private readonly ConcurrentQueue<HeartbeatEvent> _heartbeats = new();

    public IReadOnlyList<SnapshotUpdatedEvent> Snapshots => _snapshots.ToArray();

    public IReadOnlyList<DiscoveryJobStatusChangedEvent> Statuses => _statuses.ToArray();

    public IReadOnlyList<DriftApplyJobStatusChangedEvent> DriftApplyStatuses => _driftApplyStatuses.ToArray();

    public IReadOnlyList<HeartbeatEvent> Heartbeats => _heartbeats.ToArray();

    public Task PublishSnapshotUpdatedAsync(SnapshotUpdatedEvent @event, CancellationToken cancellationToken = default)
    {
        _snapshots.Enqueue(@event);
        return Task.CompletedTask;
    }

    public Task PublishJobStatusChangedAsync(DiscoveryJobStatusChangedEvent @event, CancellationToken cancellationToken = default)
    {
        _statuses.Enqueue(@event);
        return Task.CompletedTask;
    }

    public Task PublishDriftApplyJobStatusChangedAsync(DriftApplyJobStatusChangedEvent @event, CancellationToken cancellationToken = default)
    {
        _driftApplyStatuses.Enqueue(@event);
        return Task.CompletedTask;
    }

    public Task PublishHeartbeatAsync(HeartbeatEvent @event, CancellationToken cancellationToken = default)
    {
        _heartbeats.Enqueue(@event);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A publisher that always throws — used to prove the fail-open contract: a publish fault must never
/// abort ingestion or a discovery job (AC4/NFR3). The production publisher never throws, but a call site
/// must survive a broken implementation too.
/// </summary>
public sealed class ThrowingTopologyEventPublisher : ITopologyEventPublisher
{
    public Task PublishSnapshotUpdatedAsync(SnapshotUpdatedEvent @event, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("simulated Redis outage");

    public Task PublishJobStatusChangedAsync(DiscoveryJobStatusChangedEvent @event, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("simulated Redis outage");

    public Task PublishDriftApplyJobStatusChangedAsync(DriftApplyJobStatusChangedEvent @event, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("simulated Redis outage");

    public Task PublishHeartbeatAsync(HeartbeatEvent @event, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("simulated Redis outage");
}
