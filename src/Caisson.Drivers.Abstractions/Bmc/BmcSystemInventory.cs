using Caisson.Domain.Enums;

namespace Caisson.Drivers.Abstractions.Bmc;

/// <summary>
/// Identity/inventory information observed for a server's BMC. Mirrors the discoverable fields of
/// <c>Caisson.Domain.Topology.Server</c>, minus persistence identity.
/// </summary>
/// <param name="BmcType">The observed BMC management interface type.</param>
/// <param name="BmcAddress">The observed BMC address.</param>
/// <param name="BmcUuid">Observed BMC/server UUID, if known.</param>
/// <param name="Hostname">Observed hostname, if known.</param>
/// <param name="Model">Observed server hardware model, if known.</param>
/// <param name="Serial">Observed server serial number, if known.</param>
public sealed record BmcSystemInventory(
    BmcType BmcType,
    string BmcAddress,
    string? BmcUuid = null,
    string? Hostname = null,
    string? Model = null,
    string? Serial = null);
