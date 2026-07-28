namespace Caisson.Infrastructure.LiveUpdates;

/// <summary>
/// A lightweight, counts-only summary of a persisted snapshot (story #9, AC1). It carries entity counts
/// and change counts so a client can render "what changed" without the graph — it deliberately contains
/// NO graph, host, port, MAC, credentialsRef or raw device data (NFR5). Clients that need detail refetch
/// <c>GET api/racks/{rackId}/topology/snapshots/latest</c>.
/// </summary>
/// <param name="SwitchCount">Number of switches in the snapshot.</param>
/// <param name="ServerCount">Number of servers in the snapshot.</param>
/// <param name="VlanCount">Number of VLANs in the snapshot.</param>
/// <param name="Added">Total entities added versus the previous snapshot.</param>
/// <param name="Removed">Total entities removed versus the previous snapshot.</param>
/// <param name="Modified">Total entities modified versus the previous snapshot.</param>
public sealed record SnapshotSummary(
    int SwitchCount,
    int ServerCount,
    int VlanCount,
    int Added,
    int Removed,
    int Modified);
