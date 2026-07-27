using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.ReadOnly;
using Caisson.Drivers.Abstractions.Results;
using Caisson.Drivers.Abstractions.Switches;

namespace Caisson.Drivers.Abstractions.Tests.Mocks;

/// <summary>
/// A configurable in-memory <see cref="ISwitchDiscoveryDriver"/> for unit tests and as a reference
/// implementation to copy from when adding a new vendor driver (see docs/adding-a-driver.md). Each
/// method returns whatever <see cref="DriverResult{T}"/> its delegate produces (an empty success by
/// default) and honours cancellation the same way a real driver must.
/// </summary>
public sealed class MockSwitchDiscoveryDriver : ISwitchDiscoveryDriver
{
    /// <inheritdoc />
    public DriverDescriptor Descriptor { get; init; } = new("Mock", null, DriverConnectionKind.Ssh, "1.0.0-test");

    /// <summary>Canned result returned by <see cref="GetDeviceInfoAsync"/>.</summary>
    public Func<DriverResult<SwitchDeviceInfo>> DeviceInfoResult { get; set; } =
        () => DriverResult<SwitchDeviceInfo>.Ok(new SwitchDeviceInfo(null, null, null, null), TimeSpan.Zero);

    /// <summary>Canned result returned by <see cref="GetPortsAsync"/>.</summary>
    public Func<DriverResult<IReadOnlyList<SwitchPortInfo>>> PortsResult { get; set; } =
        () => DriverResult<IReadOnlyList<SwitchPortInfo>>.Ok(Array.Empty<SwitchPortInfo>(), TimeSpan.Zero);

    /// <summary>Canned result returned by <see cref="GetLldpNeighborsAsync"/>.</summary>
    public Func<DriverResult<IReadOnlyList<LldpNeighbourInfo>>> LldpNeighborsResult { get; set; } =
        () => DriverResult<IReadOnlyList<LldpNeighbourInfo>>.Ok(Array.Empty<LldpNeighbourInfo>(), TimeSpan.Zero);

    /// <summary>Canned result returned by <see cref="GetBridgeHostTableAsync"/>.</summary>
    public Func<DriverResult<IReadOnlyList<BridgeHostEntry>>> BridgeHostTableResult { get; set; } =
        () => DriverResult<IReadOnlyList<BridgeHostEntry>>.Ok(Array.Empty<BridgeHostEntry>(), TimeSpan.Zero);

    /// <summary>Canned result returned by <see cref="GetVlansAsync"/>.</summary>
    public Func<DriverResult<IReadOnlyList<VlanInfo>>> VlansResult { get; set; } =
        () => DriverResult<IReadOnlyList<VlanInfo>>.Ok(Array.Empty<VlanInfo>(), TimeSpan.Zero);

    /// <inheritdoc />
    public Task<DriverResult<SwitchDeviceInfo>> GetDeviceInfoAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DeviceInfoResult());
    }

    /// <inheritdoc />
    public Task<DriverResult<IReadOnlyList<SwitchPortInfo>>> GetPortsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PortsResult());
    }

    /// <inheritdoc />
    public Task<DriverResult<IReadOnlyList<LldpNeighbourInfo>>> GetLldpNeighborsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(LldpNeighborsResult());
    }

    /// <inheritdoc />
    public Task<DriverResult<IReadOnlyList<BridgeHostEntry>>> GetBridgeHostTableAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(BridgeHostTableResult());
    }

    /// <inheritdoc />
    public Task<DriverResult<IReadOnlyList<VlanInfo>>> GetVlansAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(VlansResult());
    }
}
