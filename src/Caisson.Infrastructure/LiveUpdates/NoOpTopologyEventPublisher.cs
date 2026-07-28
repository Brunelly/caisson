namespace Caisson.Infrastructure.LiveUpdates;

/// <summary>
/// The default <see cref="ITopologyEventPublisher"/> when live updates are disabled or Redis is
/// unconfigured (story #9, ADR 0014). It does nothing, so existing tests and any non-Redis dev host keep
/// working unchanged and the DB pipeline never hard-depends on Redis. Trivially satisfies the
/// never-throws contract.
/// </summary>
public sealed class NoOpTopologyEventPublisher : ITopologyEventPublisher
{
    /// <inheritdoc />
    public Task PublishSnapshotUpdatedAsync(SnapshotUpdatedEvent @event, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task PublishJobStatusChangedAsync(DiscoveryJobStatusChangedEvent @event, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task PublishHeartbeatAsync(HeartbeatEvent @event, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
