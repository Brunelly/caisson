namespace Caisson.Correlation.Input;

/// <summary>
/// The complete in-memory input to a correlation run: the discovered switches and servers for one
/// rack/snapshot. The engine performs no I/O — every fact it needs is present in this graph.
/// </summary>
/// <param name="Switches">The discovered switches in the snapshot.</param>
/// <param name="Servers">The discovered servers in the snapshot.</param>
public sealed record TopologyCorrelationInput(
    IReadOnlyList<SwitchTopologySnapshot> Switches,
    IReadOnlyList<ServerNicSnapshot> Servers);
