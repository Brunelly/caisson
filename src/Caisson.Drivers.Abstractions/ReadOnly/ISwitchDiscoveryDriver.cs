using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.Results;
using Caisson.Drivers.Abstractions.Switches;

namespace Caisson.Drivers.Abstractions.ReadOnly;

/// <summary>
/// Read-only discovery operations for a network switch. This interface — and every type in the
/// <see cref="ReadOnly"/> namespace — is the safety boundary from AC1/NFR1: it exposes only queries,
/// never a method that writes, configures, or otherwise mutates the device. Every method is
/// cancellable and returns a <see cref="DriverResult{T}"/> rather than throwing for expected failures.
/// </summary>
public interface ISwitchDiscoveryDriver
{
    /// <summary>Identity/capability metadata for this driver instance.</summary>
    DriverDescriptor Descriptor { get; }

    /// <summary>Reads switch-level identity and version information.</summary>
    Task<DriverResult<SwitchDeviceInfo>> GetDeviceInfoAsync(CancellationToken cancellationToken);

    /// <summary>Reads all observed ports on the switch.</summary>
    Task<DriverResult<IReadOnlyList<SwitchPortInfo>>> GetPortsAsync(CancellationToken cancellationToken);

    /// <summary>Reads observed LLDP neighbours across all ports.</summary>
    Task<DriverResult<IReadOnlyList<LldpNeighbourInfo>>> GetLldpNeighborsAsync(CancellationToken cancellationToken);

    /// <summary>Reads the bridge/MAC-address table.</summary>
    Task<DriverResult<IReadOnlyList<BridgeHostEntry>>> GetBridgeHostTableAsync(CancellationToken cancellationToken);

    /// <summary>Reads all observed VLANs configured on the switch.</summary>
    Task<DriverResult<IReadOnlyList<VlanInfo>>> GetVlansAsync(CancellationToken cancellationToken);
}
