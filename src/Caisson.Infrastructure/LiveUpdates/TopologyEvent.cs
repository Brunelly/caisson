using System.Text.Json.Serialization;

namespace Caisson.Infrastructure.LiveUpdates;

/// <summary>
/// The polymorphic envelope for every live topology event (story #9, ADR 0014). Serialized to a single
/// Redis pub/sub channel and relayed to SignalR clients, so this is the authoritative wire format: the
/// <c>type</c> discriminator plus a stable <see cref="EventId"/> that the cross-instance relay guard
/// and the client both de-duplicate on. By construction events carry only ids, versions, status,
/// counts, timestamps, seq and correlation ids — never host/port/MAC/credentialsRef or raw device data
/// (NFR5, enforced by a no-secrets contract guard test).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SnapshotUpdatedEvent), "snapshot-updated")]
[JsonDerivedType(typeof(DiscoveryJobStatusChangedEvent), "discovery-job-status-changed")]
[JsonDerivedType(typeof(HeartbeatEvent), "heartbeat")]
public abstract record TopologyEvent
{
    /// <summary>
    /// A unique id minted once at publish time and preserved across the wire, so every API instance
    /// deserializes the same id. The exactly-once relay guard keys on it (only one instance fans an
    /// event out over the SignalR backplane) and clients can use it as an extra de-dup key.
    /// </summary>
    public Guid EventId { get; init; } = Guid.NewGuid();
}
