namespace Caisson.Infrastructure.LiveUpdates;

/// <summary>
/// The Redis pub/sub channel names for live topology events (story #9, ADR 0014). Per the story's
/// answered Q1 this is a SINGLE channel carrying every event with the <c>rackId</c> in the payload; the
/// hub assigns clients to per-rack SignalR groups and dispatches accordingly, rather than subscribing to
/// per-rack Redis channels.
/// </summary>
public static class TopologyEventChannels
{
    /// <summary>The single channel every live topology event is published to.</summary>
    public const string Default = "caisson.topology.events";
}
