namespace Caisson.Infrastructure.LiveUpdates;

/// <summary>
/// A minimal server heartbeat (story #9, Q2). The server emits one every <c>HeartbeatSeconds</c> (10) so
/// a client can mark its data stale after 30s without any heartbeat or event and surface the
/// stale-data indicator (AC2). Carries no payload beyond the timestamp.
/// </summary>
/// <param name="Timestamp">When the heartbeat was emitted (UTC).</param>
public sealed record HeartbeatEvent(DateTimeOffset Timestamp) : TopologyEvent;
