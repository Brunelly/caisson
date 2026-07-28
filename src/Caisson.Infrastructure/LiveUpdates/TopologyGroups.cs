using System.Globalization;

namespace Caisson.Infrastructure.LiveUpdates;

/// <summary>
/// Names the SignalR groups clients are assigned to. Rack-scoping is a single per-rack group
/// (story #9, Q1): a client that subscribed to rack <c>X</c> only receives events fanned out to
/// <c>rack:X</c>, so a snapshot on rack A never reaches a subscriber to rack B.
/// </summary>
public static class TopologyGroups
{
    /// <summary>The SignalR group carrying events for a single rack.</summary>
    public static string ForRack(Guid rackId)
        => "rack:" + rackId.ToString("D", CultureInfo.InvariantCulture);
}
