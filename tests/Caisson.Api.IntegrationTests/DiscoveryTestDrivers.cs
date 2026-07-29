using Caisson.Domain.Enums;
using Caisson.Domain.ValueObjects;
using Caisson.Drivers.Abstractions.Bmc;
using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.Mutating;
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

/// <summary>
/// Deterministic write-capable driver factory for the story-65 drift-apply API tests. Vendor "Mock"
/// mirrors <see cref="TestSwitchDriverFactory"/>'s read-side shape, resolved instead of a real RouterOS
/// device. <see cref="Behavior"/> must be set by the test before the orchestrator is driven, since a
/// scenario's device outcome is scripted per test.
/// </summary>
internal sealed class TestSwitchMutatingDriverFactory : ISwitchMutatingDriverFactory
{
    public DriverDescriptor Descriptor { get; } = new("Mock", null, DriverConnectionKind.Ssh, "1.0.0-test");

    public Func<SetAccessVlanRequest, DriverResult<SetAccessVlanOutcome>>? Behavior { get; set; }

    public int CallCount { get; private set; }

    public ISwitchMutatingDriver Create(SwitchMutatingConnectionOptions options) => new TestSwitchMutatingDriver(this);

    internal DriverResult<SetAccessVlanOutcome> Invoke(SetAccessVlanRequest request)
    {
        CallCount++;
        return (Behavior ?? throw new InvalidOperationException(
            "TestSwitchMutatingDriverFactory.Behavior was not configured before the driver was invoked."))(request);
    }
}

internal sealed class TestSwitchMutatingDriver : ISwitchMutatingDriver
{
    private readonly TestSwitchMutatingDriverFactory _factory;

    public TestSwitchMutatingDriver(TestSwitchMutatingDriverFactory factory) => _factory = factory;

    public DriverDescriptor Descriptor => _factory.Descriptor;

    public Task<DriverResult<SetAccessVlanOutcome>> SetAccessVlanAsync(SetAccessVlanRequest request, CancellationToken cancellationToken)
        => Task.FromResult(_factory.Invoke(request));
}

/// <summary>Builds secret-free <see cref="SetAccessVlanOutcome"/> fixtures for the story-65 API tests.</summary>
internal static class TestSwitchChangeOutcomes
{
    public static DriverResult<SetAccessVlanOutcome> Ok(SetAccessVlanRequest request, SwitchChangeReasonCode reasonCode, bool confirmed = true)
    {
        var before = new SwitchAccessVlanState(request.PortName, 10, Array.Empty<int>());
        var after = new SwitchAccessVlanState(request.PortName, request.DesiredVlanId, Array.Empty<int>());
        var verification = new VerificationResult(confirmed, request.DesiredVlanId, confirmed ? request.DesiredVlanId : 10, null);
        var audit = new SwitchChangeAuditRecord(
            request.CorrelationId, "10.0.0.1", request.PortName, request.DesiredVlanId, DryRun: false,
            ConfirmWindowSeconds: 30, before, after, reasonCode, verification, DateTimeOffset.UtcNow,
            request.ActorType, request.RequestedBy);
        var outcome = new SetAccessVlanOutcome(
            "10.0.0.1", request.PortName, request.DesiredVlanId, request.CorrelationId, DryRun: false,
            new SwitchChangePlan(Array.Empty<SwitchChangeStep>()), before, after, verification, confirmed, reasonCode, audit);
        return DriverResult<SetAccessVlanOutcome>.Ok(outcome, TimeSpan.FromMilliseconds(1));
    }
}
