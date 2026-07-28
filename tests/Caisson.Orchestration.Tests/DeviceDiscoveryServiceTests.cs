using Caisson.Drivers.Abstractions.Bmc;
using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.ReadOnly;
using Caisson.Drivers.Abstractions.Registry;
using Caisson.Drivers.Abstractions.Results;
using Caisson.Drivers.Abstractions.Switches;
using Caisson.Orchestration.Discovery;
using Caisson.Orchestration.RackDefinitions;
using Caisson.Orchestration.Tests.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Caisson.Orchestration.Tests;

/// <summary>
/// DB-free tests for the read-only <see cref="DeviceDiscoveryService"/> — both the switch and BMC/server
/// folding paths (folding, partial/total failure, driver-not-found — AC5) — and the in-process
/// cancellation/nudge primitives (Q3).
/// </summary>
public sealed class DeviceDiscoveryServiceTests
{
    private static readonly DeviceDiscoveryContext Context =
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public async Task Device_discovery_folds_read_only_output_into_the_snapshot()
    {
        var driver = new MockSwitchDiscoveryDriver
        {
            PortsResult = () => DriverResult<IReadOnlyList<SwitchPortInfo>>.Ok(
                new[] { new SwitchPortInfo("ether1", true, 10, new[] { 10 }) }, TimeSpan.Zero),
        };
        var service = SwitchService(new MockSwitchDriverFactory { DriverFactory = _ => driver });
        var definition = SwitchDefinition(("sw1", "Mock", DriverConnectionKind.Ssh, "good"));

        var outcome = await service.DiscoverSwitchesAsync(definition, Context, CancellationToken.None);

        outcome.Switches.Should().ContainSingle();
        outcome.Switches[0].SwitchId.Should().Be("sw1");
        outcome.Switches[0].Ports.Should().ContainSingle(p => p.PortName == "ether1");
        outcome.Failed.Should().Be(0);
    }

    [Fact]
    public async Task Device_discovery_reports_partial_when_some_devices_fail()
    {
        var factory = new MockSwitchDriverFactory
        {
            DriverFactory = opts => opts.Host == "bad"
                ? new MockSwitchDiscoveryDriver
                {
                    DeviceInfoResult = Failing<SwitchDeviceInfo>(),
                    PortsResult = FailingList<SwitchPortInfo>(),
                    LldpNeighborsResult = FailingList<LldpNeighbourInfo>(),
                    BridgeHostTableResult = FailingList<BridgeHostEntry>(),
                    VlansResult = FailingList<VlanInfo>(),
                }
                : new MockSwitchDiscoveryDriver(),
        };
        var service = SwitchService(factory);
        var definition = SwitchDefinition(
            ("sw-good", "Mock", DriverConnectionKind.Ssh, "good"),
            ("sw-bad", "Mock", DriverConnectionKind.Ssh, "bad"));

        var outcome = await service.DiscoverSwitchesAsync(definition, Context, CancellationToken.None);

        outcome.Switches.Should().ContainSingle(s => s.SwitchId == "sw-good");
        outcome.Failed.Should().Be(1);
        outcome.IsPartial.Should().BeTrue();
    }

    [Fact]
    public async Task Device_discovery_throws_retryable_when_all_devices_fail()
    {
        var driver = new MockSwitchDiscoveryDriver
        {
            DeviceInfoResult = Failing<SwitchDeviceInfo>(retryable: true),
            PortsResult = FailingList<SwitchPortInfo>(retryable: true),
            LldpNeighborsResult = FailingList<LldpNeighbourInfo>(retryable: true),
            BridgeHostTableResult = FailingList<BridgeHostEntry>(retryable: true),
            VlansResult = FailingList<VlanInfo>(retryable: true),
        };
        var service = SwitchService(new MockSwitchDriverFactory { DriverFactory = _ => driver });
        var definition = SwitchDefinition(("sw1", "Mock", DriverConnectionKind.Ssh, "good"));

        var act = () => service.DiscoverSwitchesAsync(definition, Context, CancellationToken.None);

        (await act.Should().ThrowAsync<DiscoveryStepException>())
            .Which.Retryable.Should().BeTrue();
    }

    [Fact]
    public async Task Device_discovery_treats_missing_driver_as_a_failure()
    {
        var service = SwitchService(new MockSwitchDriverFactory());
        var definition = SwitchDefinition(("sw1", "NoSuchVendor", DriverConnectionKind.Ssh, "good"));

        var act = () => service.DiscoverSwitchesAsync(definition, Context, CancellationToken.None);

        await act.Should().ThrowAsync<DiscoveryStepException>();
    }

    [Fact]
    public async Task Server_discovery_folds_read_only_output_into_the_snapshot()
    {
        var driver = new MockBmcDiscoveryDriver
        {
            NetworkInterfacesResult = () => DriverResult<IReadOnlyList<BmcNetworkInterfaceInfo>>.Ok(
                new[] { new BmcNetworkInterfaceInfo("eth0", null) }, TimeSpan.Zero),
        };
        var service = BmcService(new MockBmcDriverFactory { DriverFactory = _ => driver });
        var definition = ServerDefinition(("srv1", "Mock", DriverConnectionKind.Redfish, "good"));

        var outcome = await service.DiscoverServersAsync(definition, Context, CancellationToken.None);

        outcome.Servers.Should().ContainSingle();
        outcome.Servers[0].ServerId.Should().Be("srv1");
        outcome.Servers[0].Nics.Should().ContainSingle(n => n.Name == "eth0");
        outcome.Failed.Should().Be(0);
    }

    [Fact]
    public async Task Server_discovery_reports_partial_when_some_devices_fail()
    {
        var factory = new MockBmcDriverFactory
        {
            DriverFactory = opts => opts.Host == "bad"
                ? new MockBmcDiscoveryDriver
                {
                    SystemInventoryResult = Failing<BmcSystemInventory>(),
                    NetworkInterfacesResult = FailingList<BmcNetworkInterfaceInfo>(),
                }
                : new MockBmcDiscoveryDriver(),
        };
        var service = BmcService(factory);
        var definition = ServerDefinition(
            ("srv-good", "Mock", DriverConnectionKind.Redfish, "good"),
            ("srv-bad", "Mock", DriverConnectionKind.Redfish, "bad"));

        var outcome = await service.DiscoverServersAsync(definition, Context, CancellationToken.None);

        outcome.Servers.Should().ContainSingle(s => s.ServerId == "srv-good");
        outcome.Failed.Should().Be(1);
        outcome.IsPartial.Should().BeTrue();
    }

    [Fact]
    public async Task Server_discovery_throws_retryable_when_all_devices_fail()
    {
        var driver = new MockBmcDiscoveryDriver
        {
            SystemInventoryResult = Failing<BmcSystemInventory>(retryable: true),
            NetworkInterfacesResult = FailingList<BmcNetworkInterfaceInfo>(retryable: true),
        };
        var service = BmcService(new MockBmcDriverFactory { DriverFactory = _ => driver });
        var definition = ServerDefinition(("srv1", "Mock", DriverConnectionKind.Redfish, "good"));

        var act = () => service.DiscoverServersAsync(definition, Context, CancellationToken.None);

        (await act.Should().ThrowAsync<DiscoveryStepException>())
            .Which.Retryable.Should().BeTrue();
    }

    [Fact]
    public async Task Server_discovery_treats_missing_driver_as_a_failure()
    {
        var service = BmcService(new MockBmcDriverFactory());
        var definition = ServerDefinition(("srv1", "NoSuchVendor", DriverConnectionKind.Redfish, "good"));

        var act = () => service.DiscoverServersAsync(definition, Context, CancellationToken.None);

        await act.Should().ThrowAsync<DiscoveryStepException>();
    }

    [Fact]
    public void Cancellation_registry_signals_only_local_jobs()
    {
        var registry = new DiscoveryCancellationRegistry();
        var jobId = Guid.NewGuid();

        registry.Signal(jobId).Should().BeFalse(); // not registered

        using var cts = registry.Register(jobId, CancellationToken.None);
        registry.Signal(jobId).Should().BeTrue();
        cts.IsCancellationRequested.Should().BeTrue();

        registry.Remove(jobId);
        registry.Signal(jobId).Should().BeFalse();
    }

    [Fact]
    public void Signal_coalesces_and_delivers_nudges()
    {
        var signal = new DiscoveryJobSignal();
        var id = Guid.NewGuid();

        signal.Notify(id);

        signal.Reader.TryRead(out var read).Should().BeTrue();
        read.Should().Be(id);
    }

    private static DeviceDiscoveryService SwitchService(MockSwitchDriverFactory factory)
        => new(
            new SwitchDriverRegistry(new ISwitchDriverFactory[] { factory }),
            new BmcDriverRegistry(Array.Empty<IBmcDriverFactory>()),
            TimeProvider.System,
            NullLogger<DeviceDiscoveryService>.Instance);

    private static DeviceDiscoveryService BmcService(MockBmcDriverFactory factory)
        => new(
            new SwitchDriverRegistry(Array.Empty<ISwitchDriverFactory>()),
            new BmcDriverRegistry(new IBmcDriverFactory[] { factory }),
            TimeProvider.System,
            NullLogger<DeviceDiscoveryService>.Instance);

    private static RackDefinition SwitchDefinition(params (string Key, string Vendor, DriverConnectionKind Kind, string Host)[] switches)
        => new(
            Guid.NewGuid(),
            "rack-key",
            switches.Select(s => new DeviceDefinition(
                s.Key, s.Vendor, null, s.Kind, s.Host, null, TimeSpan.FromSeconds(1), "kv://ref")).ToList(),
            Array.Empty<DeviceDefinition>());

    private static RackDefinition ServerDefinition(params (string Key, string Vendor, DriverConnectionKind Kind, string Host)[] servers)
        => new(
            Guid.NewGuid(),
            "rack-key",
            Array.Empty<DeviceDefinition>(),
            servers.Select(s => new DeviceDefinition(
                s.Key, s.Vendor, null, s.Kind, s.Host, null, TimeSpan.FromSeconds(1), "kv://ref")).ToList());

    private static Func<DriverResult<T>> Failing<T>(bool retryable = false)
        => () => DriverResult<T>.Fail(new DriverError(DriverErrorCode.DeviceUnreachable, "down", retryable), TimeSpan.Zero);

    private static Func<DriverResult<IReadOnlyList<T>>> FailingList<T>(bool retryable = false)
        => () => DriverResult<IReadOnlyList<T>>.Fail(
            new DriverError(DriverErrorCode.DeviceUnreachable, "down", retryable), TimeSpan.Zero);
}
