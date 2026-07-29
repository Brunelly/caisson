using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.Mutating;
using Caisson.Drivers.Abstractions.ReadOnly;
using Caisson.Drivers.Abstractions.Registry;
using Caisson.Drivers.Abstractions.Results;
using Caisson.Drivers.MikroTik;
using Caisson.Drivers.MikroTik.Credentials;
using Caisson.Drivers.MikroTik.Observability;
using Caisson.Drivers.Simulators;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Caisson.VirtualRack.IntegrationTests;

/// <summary>
/// Registered ADDITIVELY (Task #115) next to the real <c>RouterOsSwitchMutatingDriverFactory</c>, under
/// the distinct <see cref="VirtualRackApiFactory.MockWithheldVendor"/> descriptor — only a rack created
/// with <c>RackScenario.WithheldRollback</c> resolves to it (see <see cref="VirtualRackApiFactory"/>'s
/// <c>BuildDefinition</c>). Proves the ORCHESTRATION-level handling of an
/// <see cref="SwitchChangeReasonCode.AutoRolledBack"/> outcome: <c>DriftApplyOrchestrator</c> only ever
/// calls the real driver's public, synchronously-confirming <c>SetAccessVlanAsync</c> (ADR 0031's "can't
/// brick the un-bricker" safety boundary), so it can never itself observe an applied-but-unconfirmed
/// state — that raw driver-level rollback mechanism is already proven end-to-end by
/// <c>SetAccessVlanIntegrationTests</c> (referenced, not duplicated) via the internal
/// <c>BeginChangeAsync</c>/virtual-clock test seam. This double stands in for the ONE device call on this
/// ONE rack and forces the <c>AutoRolledBack</c> outcome the orchestrator cannot otherwise reach.
/// </summary>
internal sealed class ScriptedWithheldMutatingDriverFactory : ISwitchMutatingDriverFactory
{
    private readonly Func<RouterOsApiSimulator> _simulator;

    public ScriptedWithheldMutatingDriverFactory(Func<RouterOsApiSimulator> simulator) => _simulator = simulator;

    public DriverDescriptor Descriptor { get; } =
        new(VirtualRackApiFactory.MockWithheldVendor, null, DriverConnectionKind.Ssh, "1.0.0-e2e");

    /// <summary>How many times a device connection was created — used to assert no retry double-write.</summary>
    public int CallCount { get; private set; }

    public ISwitchMutatingDriver Create(SwitchMutatingConnectionOptions options)
    {
        CallCount++;
        return new ScriptedWithheldMutatingDriver(_simulator(), Descriptor);
    }
}

/// <summary>
/// Unlike mcp-tooling's precedent (which returns a hardcoded fake "before" VLAN of <c>5</c>), this double
/// reads and writes the REAL in-process <see cref="RouterOsApiSimulator"/> port state — set-then-revert to
/// the original PVID, synchronously, before returning — so a SUBSEQUENT real discovery snapshot genuinely
/// observes the reverted VLAN rather than a value this test double merely claimed. Mirrors the real
/// driver's own <c>AutoRolledBack</c> shape (<c>CheckForAutoRollbackAsync</c>): <c>After</c> reflects the
/// already-reverted PVID, exactly as the real device's own confirmed-commit scheduler would have left it
/// by the time anything reads it back.
/// </summary>
internal sealed class ScriptedWithheldMutatingDriver : ISwitchMutatingDriver
{
    private readonly RouterOsApiSimulator _simulator;

    public ScriptedWithheldMutatingDriver(RouterOsApiSimulator simulator, DriverDescriptor descriptor)
    {
        _simulator = simulator;
        Descriptor = descriptor;
    }

    public DriverDescriptor Descriptor { get; }

    public Task<DriverResult<SetAccessVlanOutcome>> SetAccessVlanAsync(
        SetAccessVlanRequest request, CancellationToken cancellationToken)
    {
        var beforePvid = _simulator.GetPortAccessVlan(request.PortName);

        // Apply, then immediately revert — never confirm. This is the withheld-confirmation outcome the
        // real device's own armed scheduler job produces once its window elapses unconfirmed (ADR 0031);
        // here it happens synchronously because the orchestrator can never itself withhold confirmation
        // through the real driver's one-shot public method.
        _simulator.SetPortAccessVlanForTest(request.PortName, request.DesiredVlanId);
        _simulator.SetPortAccessVlanForTest(request.PortName, beforePvid ?? request.DesiredVlanId);

        var before = new SwitchAccessVlanState(request.PortName, beforePvid, Array.Empty<int>());
        var after = before with { Pvid = beforePvid };
        var verification = new VerificationResult(
            false, request.DesiredVlanId, beforePvid, "withheld confirmation (e2e-scripted)");
        var occurredAt = DateTimeOffset.UtcNow;
        var audit = new SwitchChangeAuditRecord(
            request.CorrelationId, "sim-withheld", request.PortName, request.DesiredVlanId, DryRun: false,
            ConfirmWindowSeconds: 2, before, after, SwitchChangeReasonCode.AutoRolledBack, verification,
            occurredAt, request.ActorType, request.RequestedBy);
        var outcome = new SetAccessVlanOutcome(
            "sim-withheld", request.PortName, request.DesiredVlanId, request.CorrelationId, DryRun: false,
            new SwitchChangePlan(Array.Empty<SwitchChangeStep>()), before, after, verification, Confirmed: false,
            SwitchChangeReasonCode.AutoRolledBack, audit);

        return Task.FromResult(DriverResult<SetAccessVlanOutcome>.Ok(outcome, TimeSpan.FromMilliseconds(5)));
    }
}

/// <summary>
/// The read-side counterpart registered alongside <see cref="ScriptedWithheldMutatingDriverFactory"/>
/// under the same distinct vendor — without it, the <c>RackScenario.WithheldRollback</c> rack's discovery
/// would 404 (<c>DriverNotFound</c>) since the real <c>RouterOsSwitchDriverFactory</c> only answers the
/// "MikroTik" vendor. This is NOT a scripted double: it delegates every call to a real
/// <c>RouterOsSwitchDriverFactory</c>, so discovery for this one rack still talks to the REAL simulator
/// over the real RouterOS protocol — only the write path is scripted, so a "close the loop" discovery
/// after a withheld-confirmation apply genuinely observes the simulator's actual (reverted) port state.
/// </summary>
internal sealed class MockWithheldReadDriverFactory : ISwitchDriverFactory
{
    private readonly RouterOsSwitchDriverFactory _inner;

    public MockWithheldReadDriverFactory(
        ISwitchCredentialResolver credentialResolver, RouterOsMetrics metrics,
        ILoggerFactory loggerFactory, IHostEnvironment environment)
        => _inner = new RouterOsSwitchDriverFactory(credentialResolver, metrics, loggerFactory, environment);

    public DriverDescriptor Descriptor { get; } =
        new(VirtualRackApiFactory.MockWithheldVendor, null, DriverConnectionKind.Ssh, "1.0.0-e2e");

    public ISwitchDiscoveryDriver Create(SwitchConnectionOptions options) => _inner.Create(options);
}
