using Caisson.Domain.Enums;
using Caisson.Drivers.Abstractions.Bmc;
using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.ReadOnly;
using Caisson.Drivers.Abstractions.Registry;
using Caisson.Drivers.Abstractions.Results;
using Caisson.Drivers.Abstractions.Switches;

namespace Caisson.Orchestration.Tests.Fakes;

/// <summary>Delegate-driven in-memory switch driver (mirrors the abstractions-test mock shape).</summary>
public sealed class MockSwitchDiscoveryDriver : ISwitchDiscoveryDriver
{
    public DriverDescriptor Descriptor { get; init; } = new("Mock", null, DriverConnectionKind.Ssh, "1.0.0-test");

    public Func<DriverResult<SwitchDeviceInfo>> DeviceInfoResult { get; set; } =
        () => DriverResult<SwitchDeviceInfo>.Ok(new SwitchDeviceInfo(null, null, null, null), TimeSpan.Zero);

    public Func<DriverResult<IReadOnlyList<SwitchPortInfo>>> PortsResult { get; set; } =
        () => DriverResult<IReadOnlyList<SwitchPortInfo>>.Ok(Array.Empty<SwitchPortInfo>(), TimeSpan.Zero);

    public Func<DriverResult<IReadOnlyList<LldpNeighbourInfo>>> LldpNeighborsResult { get; set; } =
        () => DriverResult<IReadOnlyList<LldpNeighbourInfo>>.Ok(Array.Empty<LldpNeighbourInfo>(), TimeSpan.Zero);

    public Func<DriverResult<IReadOnlyList<BridgeHostEntry>>> BridgeHostTableResult { get; set; } =
        () => DriverResult<IReadOnlyList<BridgeHostEntry>>.Ok(Array.Empty<BridgeHostEntry>(), TimeSpan.Zero);

    public Func<DriverResult<IReadOnlyList<VlanInfo>>> VlansResult { get; set; } =
        () => DriverResult<IReadOnlyList<VlanInfo>>.Ok(Array.Empty<VlanInfo>(), TimeSpan.Zero);

    public Task<DriverResult<SwitchDeviceInfo>> GetDeviceInfoAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DeviceInfoResult());
    }

    public Task<DriverResult<IReadOnlyList<SwitchPortInfo>>> GetPortsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PortsResult());
    }

    public Task<DriverResult<IReadOnlyList<LldpNeighbourInfo>>> GetLldpNeighborsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(LldpNeighborsResult());
    }

    public Task<DriverResult<IReadOnlyList<BridgeHostEntry>>> GetBridgeHostTableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(BridgeHostTableResult());
    }

    public Task<DriverResult<IReadOnlyList<VlanInfo>>> GetVlansAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(VlansResult());
    }
}

/// <summary>Delegate-driven in-memory BMC driver (mirrors the abstractions-test mock shape).</summary>
public sealed class MockBmcDiscoveryDriver : IBmcDiscoveryDriver
{
    public DriverDescriptor Descriptor { get; init; } = new("Mock", null, DriverConnectionKind.Redfish, "1.0.0-test");

    public Func<DriverResult<BmcSystemInventory>> SystemInventoryResult { get; set; } =
        () => DriverResult<BmcSystemInventory>.Ok(new BmcSystemInventory(BmcType.Redfish, "0.0.0.0"), TimeSpan.Zero);

    public Func<DriverResult<IReadOnlyList<BmcNetworkInterfaceInfo>>> NetworkInterfacesResult { get; set; } =
        () => DriverResult<IReadOnlyList<BmcNetworkInterfaceInfo>>.Ok(Array.Empty<BmcNetworkInterfaceInfo>(), TimeSpan.Zero);

    public Func<DriverResult<BmcBiosInfo>> BiosInfoResult { get; set; } =
        () => DriverResult<BmcBiosInfo>.Ok(new BmcBiosInfo(), TimeSpan.Zero);

    public Task<DriverResult<BmcSystemInventory>> GetSystemInventoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SystemInventoryResult());
    }

    public Task<DriverResult<IReadOnlyList<BmcNetworkInterfaceInfo>>> GetNetworkInterfacesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NetworkInterfacesResult());
    }

    public Task<DriverResult<BmcBiosInfo>> GetBiosInfoAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(BiosInfoResult());
    }
}

/// <summary>Fake switch driver factory that returns a configurable mock driver.</summary>
public sealed class MockSwitchDriverFactory : ISwitchDriverFactory
{
    public DriverDescriptor Descriptor { get; init; } = new("Mock", null, DriverConnectionKind.Ssh, "1.0.0-test");

    public Func<SwitchConnectionOptions, ISwitchDiscoveryDriver> DriverFactory { get; set; } =
        _ => new MockSwitchDiscoveryDriver();

    public ISwitchDiscoveryDriver Create(SwitchConnectionOptions options) => DriverFactory(options);
}

/// <summary>Fake BMC driver factory that returns a configurable mock driver.</summary>
public sealed class MockBmcDriverFactory : IBmcDriverFactory
{
    public DriverDescriptor Descriptor { get; init; } = new("Mock", null, DriverConnectionKind.Redfish, "1.0.0-test");

    public Func<BmcConnectionOptions, IBmcDiscoveryDriver> DriverFactory { get; set; } =
        _ => new MockBmcDiscoveryDriver();

    public IBmcDiscoveryDriver Create(BmcConnectionOptions options) => DriverFactory(options);
}
