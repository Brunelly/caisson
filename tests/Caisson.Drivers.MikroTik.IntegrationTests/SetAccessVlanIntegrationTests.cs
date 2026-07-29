using System.Diagnostics;
using Caisson.Domain.Enums;
using Caisson.Drivers.Abstractions.DependencyInjection;
using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.Mutating;
using Caisson.Drivers.Abstractions.ReadOnly;
using Caisson.Drivers.Abstractions.Registry;
using Caisson.Drivers.MikroTik.Credentials;
using Caisson.Drivers.MikroTik.DependencyInjection;
using Caisson.Drivers.Simulators;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Caisson.Drivers.MikroTik.IntegrationTests;

/// <summary>
/// AC4/AC5/NFR4: end-to-end confirm-and-persist plus withhold-and-auto-rollback against the (default)
/// in-process stateful simulator, or a real CHR when opted in (<see cref="RouterOsWriteChrFixture"/>).
/// The write driver is always resolved THROUGH <see cref="ISwitchMutatingDriverRegistry"/>, mirroring
/// <c>DiscoveryIntegrationTests</c>' registry-resolution pattern.
/// </summary>
public sealed class SetAccessVlanIntegrationTests : IClassFixture<RouterOsWriteChrFixture>
{
    private static readonly TimeSpan HappyPathBudget = TimeSpan.FromSeconds(5);

    private readonly RouterOsWriteChrFixture _fixture;
    private readonly ITestOutputHelper _output;

    public SetAccessVlanIntegrationTests(RouterOsWriteChrFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task Apply_and_confirm_within_the_window_persists_and_is_visible_via_the_read_driver()
    {
        var (endpoint, simulator) = _fixture.StartStatefulSwitch(SeedPorts(), SeedVlans());

        using var writeProvider = BuildWriteProvider();
        var writeDriver = CreateMutatingDriver(writeProvider, endpoint, confirmWindow: TimeSpan.FromSeconds(2));

        var stopwatch = Stopwatch.StartNew();
        var result = await writeDriver.SetAccessVlanAsync(
            new SetAccessVlanRequest("ether1", 20, DryRun: false, ConfirmWindow: null, Guid.NewGuid(), "integration-test", ActorType.System),
            CancellationToken.None);
        stopwatch.Stop();

        result.Success.Should().BeTrue();
        result.Value!.ReasonCode.Should().Be(SwitchChangeReasonCode.Applied);
        result.Value.Confirmed.Should().BeTrue();
        stopwatch.Elapsed.Should().BeLessThan(HappyPathBudget, "NFR4: the happy-path apply+verify+confirm cycle must stay well under 5s");
        _output.WriteLine($"Apply+confirm completed in {stopwatch.ElapsedMilliseconds}ms");

        if (!_fixture.UsingRealChr)
        {
            simulator!.GetPortAccessVlan("ether1").Should().Be(20);
            simulator.HasPendingRollback("ether1").Should().BeFalse("a confirmed change must cancel its armed rollback");
        }

        // Read/write parity (AC5): a subsequent read through the REAL read-only driver against the same
        // switch shows the persisted change too.
        using var readProvider = BuildReadProvider();
        var readDriver = CreateReadDriver(readProvider, endpoint);
        var ports = await readDriver.GetPortsAsync(CancellationToken.None);
        ports.Success.Should().BeTrue();
        ports.Value!.Single(p => p.PortName == "ether1").Pvid.Should().Be(20);
    }

    [Fact]
    public async Task Withholding_confirm_past_the_window_auto_reverts_and_a_read_shows_the_original_vlan()
    {
        if (_fixture.UsingRealChr)
        {
            // Exercises the simulator's deterministic virtual clock; not meaningful against a shared real CHR.
            return;
        }

        var (endpoint, simulator) = _fixture.StartStatefulSwitch(SeedPorts(), SeedVlans());

        using var provider = BuildWriteProvider();
        var mutatingDriver = CreateMutatingDriver(provider, endpoint, confirmWindow: TimeSpan.FromSeconds(2));
        var driver = Assert.IsType<RouterOsSwitchMutatingDriver>(mutatingDriver);

        var request = new SetAccessVlanRequest(
            "ether1", 20, DryRun: false, ConfirmWindow: null, Guid.NewGuid(), "integration-test", ActorType.System);

        // Apply and verify via the internal Begin seam, but deliberately never confirm — simulating a
        // crash between a successful apply and the confirm signal (AC4).
        var pending = await driver.BeginChangeAsync(request, CancellationToken.None);

        pending.Result.Success.Should().BeTrue();
        pending.Result.Value!.ReasonCode.Should().Be(SwitchChangeReasonCode.Applied);
        pending.Result.Value.Confirmed.Should().BeFalse();
        simulator!.GetPortAccessVlan("ether1").Should().Be(20, "the change is applied even though it is not yet confirmed");
        simulator.HasPendingRollback("ether1").Should().BeTrue();

        // Advance the simulator's virtual clock past the 2s confirm window and fire the due rollback —
        // no real sleep.
        simulator.AdvanceTime(TimeSpan.FromSeconds(3));
        simulator.FireDueRollbacks();

        simulator.GetPortAccessVlan("ether1").Should().Be(10, "the device (simulator) must self-revert once the window elapses unconfirmed");
        simulator.HasPendingRollback("ether1").Should().BeFalse();

        // A follow-up verification (AC4's own allowance: "a result or follow-up verification") surfaces
        // AutoRolledBack, since the driver has no way to observe the asynchronous device-side revert
        // during the original call.
        var rollbackCheck = await driver.CheckForAutoRollbackAsync(pending.Result.Value, CancellationToken.None);
        rollbackCheck.Success.Should().BeTrue();
        rollbackCheck.Value!.ReasonCode.Should().Be(SwitchChangeReasonCode.AutoRolledBack);

        // A subsequent read through the real read-only driver against the same switch also shows the
        // reverted value (AC4's explicit "a subsequent read shows the port access VLAN is VLAN 10").
        using var readProvider = BuildReadProvider();
        var readDriver = CreateReadDriver(readProvider, endpoint);
        var ports = await readDriver.GetPortsAsync(CancellationToken.None);
        ports.Value!.Single(p => p.PortName == "ether1").Pvid.Should().Be(10);
    }

    [Fact]
    public async Task Idempotent_apply_to_the_current_vlan_is_a_noop_and_leaves_state_unchanged()
    {
        var (endpoint, simulator) = _fixture.StartStatefulSwitch(SeedPorts(), SeedVlans());

        using var provider = BuildWriteProvider();
        var driver = CreateMutatingDriver(provider, endpoint, confirmWindow: TimeSpan.FromSeconds(2));

        var result = await driver.SetAccessVlanAsync(
            new SetAccessVlanRequest("ether1", 10, DryRun: false, ConfirmWindow: null, Guid.NewGuid(), "integration-test", ActorType.System),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Value!.ReasonCode.Should().Be(SwitchChangeReasonCode.NoOpAlreadyDesiredState);

        if (!_fixture.UsingRealChr)
        {
            simulator!.GetPortAccessVlan("ether1").Should().Be(10);
            simulator.HasPendingRollback("ether1").Should().BeFalse();
        }
    }

    [Fact]
    public async Task Invalid_vlan_is_rejected_and_the_port_is_unchanged()
    {
        var (endpoint, simulator) = _fixture.StartStatefulSwitch(SeedPorts(), SeedVlans());

        using var provider = BuildWriteProvider();
        var driver = CreateMutatingDriver(provider, endpoint, confirmWindow: TimeSpan.FromSeconds(2));

        var result = await driver.SetAccessVlanAsync(
            new SetAccessVlanRequest("ether1", 4095, DryRun: false, ConfirmWindow: null, Guid.NewGuid(), "integration-test", ActorType.System),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Value!.ReasonCode.Should().Be(SwitchChangeReasonCode.InvalidVlanId);

        if (!_fixture.UsingRealChr)
        {
            simulator!.GetPortAccessVlan("ether1").Should().Be(10);
        }
    }

    private static Dictionary<string, int> SeedPorts() => new(StringComparer.Ordinal) { ["ether1"] = 10 };

    private static Dictionary<int, SimulatorVlanMembership> SeedVlans() => new()
    {
        [10] = new SimulatorVlanMembership(Array.Empty<string>(), new[] { "ether1" }),
        [20] = new SimulatorVlanMembership(Array.Empty<string>(), Array.Empty<string>()),
    };

    private ISwitchMutatingDriver CreateMutatingDriver(ServiceProvider provider, RouterOsEndpoint endpoint, TimeSpan confirmWindow)
    {
        var registry = provider.GetRequiredService<ISwitchMutatingDriverRegistry>();
        var query = new DriverDescriptor("MikroTik", null, DriverConnectionKind.RouterOsApi, "0.0.0-any");
        registry.TryResolve(query, out var factory).Should().BeTrue();

        var options = new SwitchMutatingConnectionOptions(
            endpoint.Host, endpoint.Port, TimeSpan.FromSeconds(2), "core_switch",
            UseTls: false, AllowPlaintext: true, ConfirmWindow: confirmWindow);
        return factory!.Create(options);
    }

    private ISwitchDiscoveryDriver CreateReadDriver(ServiceProvider provider, RouterOsEndpoint endpoint)
    {
        var registry = provider.GetRequiredService<ISwitchDriverRegistry>();
        var query = new DriverDescriptor("MikroTik", null, DriverConnectionKind.RouterOsApi, "0.0.0-any");
        registry.TryResolve(query, out var factory).Should().BeTrue();

        var options = new SwitchConnectionOptions(
            endpoint.Host, endpoint.Port, TimeSpan.FromSeconds(2), "core_switch", UseTls: false, AllowPlaintext: true);
        return factory!.Create(options);
    }

    private ServiceProvider BuildWriteProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(new XunitLoggerFactory(_output));
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton<ISwitchCredentialResolver>(_ => new EnvSwitchCredentialResolver(_fixture.EnvLookup));
        services.AddMikroTikRouterOsSwitchMutatingDriver();
        services.AddCaissonDriverRegistry();
        return services.BuildServiceProvider();
    }

    private ServiceProvider BuildReadProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(new XunitLoggerFactory(_output));
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton<ISwitchCredentialResolver>(_ => new EnvSwitchCredentialResolver(_fixture.EnvLookup));
        services.AddMikroTikRouterOsSwitchDriver();
        services.AddCaissonDriverRegistry();
        return services.BuildServiceProvider();
    }
}
