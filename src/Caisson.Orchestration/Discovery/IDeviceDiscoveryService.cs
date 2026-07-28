using Caisson.Correlation.Input;
using Caisson.Orchestration.RackDefinitions;

namespace Caisson.Orchestration.Discovery;

/// <summary>
/// Invokes the read-only switch/BMC discovery drivers for a rack's devices and folds their output into
/// the correlation-engine input records (story #8, AC5). Only the <c>ReadOnly</c> driver interfaces are
/// reachable from here, and every driver call is logged as a <c>ReadOnly</c> operation.
/// </summary>
public interface IDeviceDiscoveryService
{
    /// <summary>Discovers all switches in the rack definition.</summary>
    /// <exception cref="DiscoveryStepException">Thrown when every switch fails discovery.</exception>
    Task<SwitchDiscoveryOutcome> DiscoverSwitchesAsync(
        RackDefinition definition, DeviceDiscoveryContext context, CancellationToken cancellationToken);

    /// <summary>Discovers all servers (BMCs) in the rack definition.</summary>
    /// <exception cref="DiscoveryStepException">Thrown when every server fails discovery.</exception>
    Task<ServerDiscoveryOutcome> DiscoverServersAsync(
        RackDefinition definition, DeviceDiscoveryContext context, CancellationToken cancellationToken);
}

/// <summary>The traceability context threaded through every driver call log line (NFR4).</summary>
/// <param name="CorrelationId">The job's correlation id.</param>
/// <param name="RackId">The rack under discovery.</param>
/// <param name="JobId">The discovery job id.</param>
public readonly record struct DeviceDiscoveryContext(Guid CorrelationId, Guid RackId, Guid JobId);

/// <summary>The outcome of discovering a rack's switches.</summary>
/// <param name="Switches">The per-switch snapshots for the devices that produced usable data.</param>
/// <param name="Attempted">The number of switches attempted.</param>
/// <param name="Failed">The number of switches that produced no usable data.</param>
public sealed record SwitchDiscoveryOutcome(
    IReadOnlyList<SwitchTopologySnapshot> Switches, int Attempted, int Failed)
{
    /// <summary>Whether at least one device failed while others succeeded.</summary>
    public bool IsPartial => Failed > 0 && Switches.Count > 0;
}

/// <summary>The outcome of discovering a rack's servers.</summary>
/// <param name="Servers">The per-server snapshots for the devices that produced usable data.</param>
/// <param name="Attempted">The number of servers attempted.</param>
/// <param name="Failed">The number of servers that produced no usable data.</param>
public sealed record ServerDiscoveryOutcome(
    IReadOnlyList<ServerNicSnapshot> Servers, int Attempted, int Failed)
{
    /// <summary>Whether at least one device failed while others succeeded.</summary>
    public bool IsPartial => Failed > 0 && Servers.Count > 0;
}
