using Caisson.Domain.ValueObjects;

namespace Caisson.Drivers.Abstractions.Switches;

/// <summary>An observed bridge/MAC-address-table entry reported by a switch driver.</summary>
/// <param name="PortName">The port the MAC was learned on.</param>
/// <param name="Mac">The normalized MAC address, reusing <see cref="MacAddressValue"/> from the domain.</param>
public sealed record BridgeHostEntry(string PortName, MacAddressValue Mac);
