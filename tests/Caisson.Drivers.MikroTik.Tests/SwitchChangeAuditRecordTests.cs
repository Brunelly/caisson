using Caisson.Domain.Enums;
using Caisson.Drivers.Abstractions.Mutating;
using Caisson.Drivers.MikroTik.Observability;
using Caisson.Drivers.MikroTik.Tests.Fixtures;
using Caisson.Drivers.MikroTik.Transport;
using FluentAssertions;
using Xunit;

namespace Caisson.Drivers.MikroTik.Tests;

/// <summary>
/// AC6: a <see cref="SwitchChangeAuditRecord"/> is produced on every outcome with complete fields, a
/// deterministic timestamp (via an injected <see cref="TimeProvider"/>, mirroring the codebase's
/// hand-rolled <c>FixedTimeProvider</c> convention), and never leaks secret material even when an
/// underlying exception message contains it.
/// </summary>
public sealed class SwitchChangeAuditRecordTests
{
    private const string Password = "hunter2-SUPER-secret";

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTime now) => _now = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc));

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static RouterOsSwitchMutatingDriver DriverFor(
        FakeRouterOsWriteApiClient client, TimeProvider? timeProvider = null, CapturingLogger<RouterOsSwitchMutatingDriver>? logger = null)
        => new("10.0.0.1", () => client, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30),
            new RouterOsWriteMetrics(), timeProvider ?? TimeProvider.System,
            logger ?? new CapturingLogger<RouterOsSwitchMutatingDriver>());

    private static SetAccessVlanRequest Request(string port, int vlanId, bool dryRun = false)
        => new(port, vlanId, dryRun, TimeSpan.FromSeconds(15), Guid.NewGuid(), "operator@example.com", ActorType.ServiceAccount);

    [Fact]
    public async Task Dry_run_audit_record_has_full_field_completeness()
    {
        var fixedNow = new DateTime(2026, 1, 1, 12, 0, 0);
        var client = new FakeRouterOsWriteApiClient();
        client.SetRows(RouterOsWriteCommands.BridgePortPrint, new[]
        {
            RouterOsFixtures.Row((".id", "*1"), ("interface", "ether1"), ("pvid", "10")),
        });
        client.SetRows(RouterOsWriteCommands.BridgeVlanPrint, new[]
        {
            RouterOsFixtures.Row(("vlan-ids", "10"), ("untagged", "ether1")),
            RouterOsFixtures.Row(("vlan-ids", "20"), ("untagged", string.Empty)),
        });

        var request = Request("ether1", 20, dryRun: true);
        var result = await DriverFor(client, new FixedTimeProvider(fixedNow)).SetAccessVlanAsync(request, CancellationToken.None);

        var audit = result.Value!.Audit;
        audit.CorrelationId.Should().Be(request.CorrelationId);
        audit.DeviceHost.Should().Be("10.0.0.1");
        audit.PortName.Should().Be("ether1");
        audit.VlanId.Should().Be(20);
        audit.DryRun.Should().BeTrue();
        audit.ConfirmWindowSeconds.Should().Be(15);
        audit.Before.Should().NotBeNull();
        audit.After.Should().NotBeNull();
        audit.ReasonCode.Should().Be(SwitchChangeReasonCode.DryRunPlanned);
        audit.OccurredAtUtc.Should().Be(new DateTimeOffset(fixedNow, TimeSpan.Zero));
        audit.ActorType.Should().Be(ActorType.ServiceAccount);
        audit.RequestedBy.Should().Be("operator@example.com");
    }

    [Fact]
    public async Task Applied_audit_record_carries_verification_and_before_after_subsets()
    {
        var client = new FakeRouterOsWriteApiClient();
        var currentPvid = 10;
        client.SetHandler(RouterOsWriteCommands.BridgePortPrint, _ => new[]
        {
            RouterOsFixtures.Row((".id", "*1"), ("interface", "ether1"), ("pvid", currentPvid.ToString())),
        });
        client.SetRows(RouterOsWriteCommands.BridgeVlanPrint, new[]
        {
            RouterOsFixtures.Row(("vlan-ids", "10"), ("untagged", "ether1")),
            RouterOsFixtures.Row(("vlan-ids", "20"), ("untagged", string.Empty)),
        });
        client.SetHandler(RouterOsWriteCommands.BridgePortSet, words =>
        {
            currentPvid = int.Parse(words.Single(w => w.StartsWith("=pvid=", StringComparison.Ordinal))["=pvid=".Length..]);
            return Array.Empty<IReadOnlyDictionary<string, string>>();
        });

        var result = await DriverFor(client).SetAccessVlanAsync(Request("ether1", 20), CancellationToken.None);

        var audit = result.Value!.Audit;
        audit.ReasonCode.Should().Be(SwitchChangeReasonCode.Applied);
        audit.Before!.Pvid.Should().Be(10);
        audit.After!.Pvid.Should().Be(20);
        audit.Verification!.Verified.Should().BeTrue();
        audit.Verification.ObservedVlanId.Should().Be(20);
    }

    [Fact]
    public async Task Rejected_request_audit_record_still_captures_the_attempted_intent()
    {
        var client = new FakeRouterOsWriteApiClient();

        var request = Request("ether1", 4095);
        var result = await DriverFor(client).SetAccessVlanAsync(request, CancellationToken.None);

        var audit = result.Value!.Audit;
        audit.ReasonCode.Should().Be(SwitchChangeReasonCode.InvalidVlanId);
        audit.VlanId.Should().Be(4095);
        audit.PortName.Should().Be("ether1");
        audit.CorrelationId.Should().Be(request.CorrelationId);
    }

    [Fact]
    public async Task No_secret_material_appears_in_the_driver_error_or_captured_logs()
    {
        var logger = new CapturingLogger<RouterOsSwitchMutatingDriver>();
        var client = new FakeRouterOsWriteApiClient
        {
            OnConnect = () => throw new RouterOsAuthenticationException($"login rejected for password={Password}"),
        };

        var result = await DriverFor(client, logger: logger).SetAccessVlanAsync(Request("ether1", 20), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error!.Message.Should().NotContain(Password);
        logger.AllText.Should().NotContain(Password);
    }
}
