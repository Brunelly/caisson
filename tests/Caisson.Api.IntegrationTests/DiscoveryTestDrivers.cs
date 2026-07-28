using Caisson.Domain.Enums;
using Caisson.Domain.ValueObjects;
using Caisson.Drivers.Abstractions.Bmc;
using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.ReadOnly;
using Caisson.Drivers.Abstractions.Registry;
using Caisson.Drivers.Abstractions.Results;
using Caisson.Drivers.Abstractions.Switches;
using Caisson.Orchestration.RackDefinitions;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// A test rack-definition provider that returns a Mock switch + Mock server definition for ANY rack, so
/// e2e tests can trigger discovery on freshly-created racks without editing config.
/// </summary>
internal sealed class TestRackDefinitionProvider : IRackDefinitionProvider
{
    public Task<RackDefinition> GetAsync(Guid rackId, CancellationToken cancellationToken)
        => Task.FromResult(new RackDefinition(
            rackId,
            "test",
            new[]
            {
                new DeviceDefinition("sw1", "Mock", null, DriverConnectionKind.Ssh, "10.0.0.1", null,
                    TimeSpan.FromSeconds(2), "kv://switch/ref"),
            },
            new[]
            {
                new DeviceDefinition("srv1", "Mock", null, DriverConnectionKind.Redfish, "10.0.1.1", null,
                    TimeSpan.FromSeconds(2), "kv://bmc/ref"),
            }));
}

/// <summary>
/// Deterministic in-memory drivers for the API discovery e2e test — the orchestrator resolves these
/// (Vendor "Mock") instead of reaching a real device, so a triggered job runs to a terminal state and
/// persists a snapshot without any hardware.
/// </summary>
internal sealed class TestSwitchDriverFactory : ISwitchDriverFactory
{
    public DriverDescriptor Descriptor { get; } = new("Mock", null, DriverConnectionKind.Ssh, "1.0.0-test");

    public ISwitchDiscoveryDriver Create(SwitchConnectionOptions options) => new TestSwitchDriver();
}

internal sealed class TestBmcDriverFactory : IBmcDriverFactory
{
    public DriverDescriptor Descriptor { get; } = new("Mock", null, DriverConnectionKind.Redfish, "1.0.0-test");

    public IBmcDiscoveryDriver Create(BmcConnectionOptions options) => new TestBmcDriver();
}

internal sealed class TestSwitchDriver : ISwitchDiscoveryDriver
{
    private static readonly MacAddressValue Mac = MacAddressValue.Parse("aa:aa:aa:aa:aa:01");

    public DriverDescriptor Descriptor { get; } = new("Mock", null, DriverConnectionKind.Ssh, "1.0.0-test");

    public Task<DriverResult<SwitchDeviceInfo>> GetDeviceInfoAsync(CancellationToken cancellationToken)
        => Task.FromResult(DriverResult<SwitchDeviceInfo>.Ok(
            new SwitchDeviceInfo("10.0.0.1", "SW-TEST", "CRS", "7.15"), TimeSpan.Zero));

    public Task<DriverResult<IReadOnlyList<SwitchPortInfo>>> GetPortsAsync(CancellationToken cancellationToken)
        => Task.FromResult(DriverResult<IReadOnlyList<SwitchPortInfo>>.Ok(
            new[] { new SwitchPortInfo("ether1", true, 10, new[] { 10 }) }, TimeSpan.Zero));

    public Task<DriverResult<IReadOnlyList<LldpNeighbourInfo>>> GetLldpNeighborsAsync(CancellationToken cancellationToken)
        => Task.FromResult(DriverResult<IReadOnlyList<LldpNeighbourInfo>>.Ok(
            Array.Empty<LldpNeighbourInfo>(), TimeSpan.Zero));

    public Task<DriverResult<IReadOnlyList<BridgeHostEntry>>> GetBridgeHostTableAsync(CancellationToken cancellationToken)
        => Task.FromResult(DriverResult<IReadOnlyList<BridgeHostEntry>>.Ok(
            new[] { new BridgeHostEntry("ether1", Mac) }, TimeSpan.Zero));

    public Task<DriverResult<IReadOnlyList<VlanInfo>>> GetVlansAsync(CancellationToken cancellationToken)
        => Task.FromResult(DriverResult<IReadOnlyList<VlanInfo>>.Ok(
            new[] { new VlanInfo(10, "data") }, TimeSpan.Zero));
}

internal sealed class TestBmcDriver : IBmcDiscoveryDriver
{
    private static readonly MacAddressValue Mac = MacAddressValue.Parse("aa:aa:aa:aa:aa:01");

    public DriverDescriptor Descriptor { get; } = new("Mock", null, DriverConnectionKind.Redfish, "1.0.0-test");

    public Task<DriverResult<BmcSystemInventory>> GetSystemInventoryAsync(CancellationToken cancellationToken)
        => Task.FromResult(DriverResult<BmcSystemInventory>.Ok(
            new BmcSystemInventory(BmcType.Redfish, "10.0.1.1", "uuid-test", "host-test"), TimeSpan.Zero));

    public Task<DriverResult<IReadOnlyList<BmcNetworkInterfaceInfo>>> GetNetworkInterfacesAsync(CancellationToken cancellationToken)
        => Task.FromResult(DriverResult<IReadOnlyList<BmcNetworkInterfaceInfo>>.Ok(
            new[] { new BmcNetworkInterfaceInfo("eth0", Mac, LinkState.Up) }, TimeSpan.Zero));

    public Task<DriverResult<BmcBiosInfo>> GetBiosInfoAsync(CancellationToken cancellationToken)
        => Task.FromResult(DriverResult<BmcBiosInfo>.Ok(new BmcBiosInfo(), TimeSpan.Zero));
}
