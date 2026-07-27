using Caisson.Drivers.Abstractions.Bmc;
using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.Results;

namespace Caisson.Drivers.Abstractions.ReadOnly;

/// <summary>
/// Read-only discovery operations for a server's baseboard management controller (Redfish/IPMI).
/// Part of the safety boundary from AC1/NFR1 (see <see cref="ISwitchDiscoveryDriver"/>): only
/// queries, no power control or configuration methods. Every method is cancellable and returns a
/// <see cref="DriverResult{T}"/> rather than throwing for expected failures.
/// </summary>
public interface IBmcDiscoveryDriver
{
    /// <summary>Identity/capability metadata for this driver instance.</summary>
    DriverDescriptor Descriptor { get; }

    /// <summary>Reads server identity/inventory information as observed via the BMC.</summary>
    Task<DriverResult<BmcSystemInventory>> GetSystemInventoryAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads all observed network interfaces and their MAC addresses. This is the sole source of a
    /// BMC's NIC MAC inventory; each returned interface already carries its own MAC.
    /// </summary>
    Task<DriverResult<IReadOnlyList<BmcNetworkInterfaceInfo>>> GetNetworkInterfacesAsync(
        CancellationToken cancellationToken);

    /// <summary>Reads observed BIOS/firmware information.</summary>
    Task<DriverResult<BmcBiosInfo>> GetBiosInfoAsync(CancellationToken cancellationToken);
}
