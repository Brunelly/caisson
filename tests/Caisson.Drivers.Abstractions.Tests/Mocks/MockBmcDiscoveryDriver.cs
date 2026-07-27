using Caisson.Domain.Enums;
using Caisson.Drivers.Abstractions.Bmc;
using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.ReadOnly;
using Caisson.Drivers.Abstractions.Results;

namespace Caisson.Drivers.Abstractions.Tests.Mocks;

/// <summary>
/// A configurable in-memory <see cref="IBmcDiscoveryDriver"/> for unit tests and as a reference
/// implementation to copy from when adding a new vendor driver (see docs/adding-a-driver.md). Each
/// method returns whatever <see cref="DriverResult{T}"/> its delegate produces (an empty success by
/// default) and honours cancellation the same way a real driver must.
/// </summary>
public sealed class MockBmcDiscoveryDriver : IBmcDiscoveryDriver
{
    /// <inheritdoc />
    public DriverDescriptor Descriptor { get; init; } = new("Mock", null, DriverConnectionKind.Redfish, "1.0.0-test");

    /// <summary>Canned result returned by <see cref="GetSystemInventoryAsync"/>.</summary>
    public Func<DriverResult<BmcSystemInventory>> SystemInventoryResult { get; set; } =
        () => DriverResult<BmcSystemInventory>.Ok(
            new BmcSystemInventory(BmcType.Redfish, "0.0.0.0"), TimeSpan.Zero);

    /// <summary>Canned result returned by <see cref="GetNetworkInterfacesAsync"/>.</summary>
    public Func<DriverResult<IReadOnlyList<BmcNetworkInterfaceInfo>>> NetworkInterfacesResult { get; set; } =
        () => DriverResult<IReadOnlyList<BmcNetworkInterfaceInfo>>.Ok(
            Array.Empty<BmcNetworkInterfaceInfo>(), TimeSpan.Zero);

    /// <summary>Canned result returned by <see cref="GetBiosInfoAsync"/>.</summary>
    public Func<DriverResult<BmcBiosInfo>> BiosInfoResult { get; set; } =
        () => DriverResult<BmcBiosInfo>.Ok(new BmcBiosInfo(), TimeSpan.Zero);

    /// <inheritdoc />
    public Task<DriverResult<BmcSystemInventory>> GetSystemInventoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SystemInventoryResult());
    }

    /// <inheritdoc />
    public Task<DriverResult<IReadOnlyList<BmcNetworkInterfaceInfo>>> GetNetworkInterfacesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NetworkInterfacesResult());
    }

    /// <inheritdoc />
    public Task<DriverResult<BmcBiosInfo>> GetBiosInfoAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(BiosInfoResult());
    }
}
