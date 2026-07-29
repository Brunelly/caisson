using Caisson.Infrastructure.LiveUpdates;

namespace Caisson.Api.Realtime.Hubs;

/// <summary>
/// The strongly-typed <b>server → client</b> surface of the topology hub (story #9). These are the ONLY
/// messages the server pushes; the hub carries no client → server method that mutates state or triggers
/// discovery (that invariant is asserted by a reflection guard over <see cref="TopologyHub"/>).
/// </summary>
public interface ITopologyClient
{
    /// <summary>A new snapshot was persisted for a subscribed rack.</summary>
    Task SnapshotUpdated(SnapshotUpdatedEvent @event);

    /// <summary>A discovery job for a subscribed rack changed status.</summary>
    Task DiscoveryJobStatusChanged(DiscoveryJobStatusChangedEvent @event);

    /// <summary>A drift-apply job for a subscribed rack changed status (story #65, AC7).</summary>
    Task DriftApplyJobStatusChanged(DriftApplyJobStatusChangedEvent @event);

    /// <summary>A liveness heartbeat (every 10s) so clients can detect staleness.</summary>
    Task Heartbeat(HeartbeatEvent @event);
}
