using Caisson.Drivers.Abstractions.Bmc;

namespace Caisson.Correlation.Input;

/// <summary>
/// A read-only snapshot of one server's BMC discovery output, assembled from the story-3
/// <c>IBmcDiscoveryDriver</c> info records. The engine keys the server by <paramref name="ServerId"/>
/// (a caller-supplied stable identifier) since the driver records carry no persistence identity.
/// </summary>
/// <param name="ServerId">Caller-supplied stable identifier for the server.</param>
/// <param name="System">Observed BMC/server identity/inventory, if known.</param>
/// <param name="Nics">Observed network interfaces (each carrying its own MAC, if parseable).</param>
public sealed record ServerNicSnapshot(
    string ServerId,
    BmcSystemInventory? System,
    IReadOnlyList<BmcNetworkInterfaceInfo> Nics);
