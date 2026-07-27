using Caisson.Domain.Enums;
using Caisson.Domain.ValueObjects;

namespace Caisson.Drivers.Abstractions.Bmc;

/// <summary>
/// An observed network interface on a server, as reported by a BMC driver. Mirrors
/// <c>Caisson.Domain.Topology.Nic</c>. This is the single source of truth for a BMC's NIC MAC
/// inventory — there is no separate "ethernet MACs" method, since every interface here already
/// carries its own MAC.
/// </summary>
/// <param name="Name">Observed interface name.</param>
/// <param name="Mac">The normalized MAC address, reusing <see cref="MacAddressValue"/> from the domain.</param>
/// <param name="LinkState">Observed link state, if known.</param>
public sealed record BmcNetworkInterfaceInfo(string Name, MacAddressValue Mac, LinkState? LinkState = null);
