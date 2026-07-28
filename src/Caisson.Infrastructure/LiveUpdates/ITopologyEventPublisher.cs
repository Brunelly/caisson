namespace Caisson.Infrastructure.LiveUpdates;

/// <summary>
/// The dependency-free real-time seam the persistence and orchestration layers call when topology state
/// changes (story #9, ADR 0014). Abstracted so those layers never reference SignalR or Redis: the
/// production implementation publishes to a Redis pub/sub channel, while a no-op implementation is the
/// default when live updates are disabled or Redis is unconfigured (so the DB pipeline never hard-depends
/// on Redis).
/// <para>
/// HARD CONTRACT: implementations MUST NEVER throw. This is the fail-open seam that keeps discovery alive
/// through a Redis outage (AC4/NFR3) — a publish fault is logged as a structured warning and swallowed,
/// never propagated, so it can never abort or crash ingestion or a discovery job.
/// </para>
/// </summary>
public interface ITopologyEventPublisher
{
    /// <summary>Publishes a snapshot-updated event. Never throws (see the type contract).</summary>
    Task PublishSnapshotUpdatedAsync(SnapshotUpdatedEvent @event, CancellationToken cancellationToken = default);

    /// <summary>Publishes a discovery-job-status-changed event. Never throws (see the type contract).</summary>
    Task PublishJobStatusChangedAsync(DiscoveryJobStatusChangedEvent @event, CancellationToken cancellationToken = default);

    /// <summary>Publishes a heartbeat event. Never throws (see the type contract).</summary>
    Task PublishHeartbeatAsync(HeartbeatEvent @event, CancellationToken cancellationToken = default);
}
