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
/// <param name="Mac">
/// The normalized MAC address, reusing <see cref="MacAddressValue"/> from the domain, or <c>null</c> when
/// the BMC reported the interface but not a parseable MAC. A MAC-less interface is still returned (rather
/// than dropped) so the gap stays visible for correlation debugging, with a per-NIC
/// <see cref="Results.DriverDiagnostic"/> attached to the result naming the interface (story #5 answered
/// question; see ADR 0009).
/// </param>
/// <param name="LinkState">Observed link state, if known.</param>
public sealed record BmcNetworkInterfaceInfo(string Name, MacAddressValue? Mac, LinkState? LinkState = null);
