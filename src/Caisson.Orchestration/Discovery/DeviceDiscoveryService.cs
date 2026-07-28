using Caisson.Correlation.Input;
using Caisson.Domain.Enums;
using Caisson.Drivers.Abstractions.Bmc;
using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.ReadOnly;
using Caisson.Drivers.Abstractions.Registry;
using Caisson.Drivers.Abstractions.Results;
using Caisson.Drivers.Abstractions.Switches;
using Caisson.Orchestration.RackDefinitions;
using Microsoft.Extensions.Logging;

namespace Caisson.Orchestration.Discovery;

/// <summary>
/// Resolves each device's driver through the DI-populated registries, invokes only the <c>ReadOnly</c>
/// discovery methods, and folds each <see cref="DriverResult{T}"/> into the correlation input records.
/// Every driver call is logged with the full traceability set and a hardcoded
/// <c>OperationCategory=ReadOnly</c> — the observable fact that satisfies AC5.
/// </summary>
public sealed class DeviceDiscoveryService : IDeviceDiscoveryService
{
    private const string ReadOnlyCategory = "ReadOnly";

    private readonly ISwitchDriverRegistry _switchRegistry;
    private readonly IBmcDriverRegistry _bmcRegistry;
    private readonly TimeProvider _time;
    private readonly ILogger<DeviceDiscoveryService> _logger;

    public DeviceDiscoveryService(
        ISwitchDriverRegistry switchRegistry,
        IBmcDriverRegistry bmcRegistry,
        TimeProvider time,
        ILogger<DeviceDiscoveryService> logger)
    {
        _switchRegistry = switchRegistry ?? throw new ArgumentNullException(nameof(switchRegistry));
        _bmcRegistry = bmcRegistry ?? throw new ArgumentNullException(nameof(bmcRegistry));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<SwitchDiscoveryOutcome> DiscoverSwitchesAsync(
        RackDefinition definition, DeviceDiscoveryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var snapshots = new List<SwitchTopologySnapshot>();
        var failed = 0;
        var anyRetryable = false;

        foreach (var device in definition.Switches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_switchRegistry.TryResolve(device.ToDescriptor(), out var factory))
            {
                failed++;
                LogDriverNotFound(context, device, DiscoveryStepName.SwitchDiscovery);
                continue;
            }

            var driver = factory.Create(
                new SwitchConnectionOptions(device.Host, device.Port, device.Timeout, device.CredentialsRef));

            var deviceInfo = await InvokeAsync(
                context, device.DeviceKey, DiscoveryStepName.SwitchDiscovery, "device-info",
                driver.GetDeviceInfoAsync, cancellationToken);
            var ports = await InvokeAsync(
                context, device.DeviceKey, DiscoveryStepName.SwitchDiscovery, "ports",
                driver.GetPortsAsync, cancellationToken);
            var lldp = await InvokeAsync(
                context, device.DeviceKey, DiscoveryStepName.SwitchDiscovery, "lldp",
                driver.GetLldpNeighborsAsync, cancellationToken);
            var bridge = await InvokeAsync(
                context, device.DeviceKey, DiscoveryStepName.SwitchDiscovery, "bridge",
                driver.GetBridgeHostTableAsync, cancellationToken);
            var vlans = await InvokeAsync(
                context, device.DeviceKey, DiscoveryStepName.SwitchDiscovery, "vlans",
                driver.GetVlansAsync, cancellationToken);

            var results = new IDriverOutcome[] { deviceInfo, ports, lldp, bridge, vlans };
            if (results.All(r => !r.Success))
            {
                failed++;
                anyRetryable |= results.Any(r => r.Retryable);
                continue;
            }

            snapshots.Add(new SwitchTopologySnapshot(
                device.DeviceKey,
                deviceInfo.Success ? deviceInfo.Value : null,
                ports.Success ? ports.Value! : Array.Empty<SwitchPortInfo>(),
                lldp.Success ? lldp.Value! : Array.Empty<LldpNeighbourInfo>(),
                bridge.Success ? bridge.Value! : Array.Empty<BridgeHostEntry>(),
                vlans.Success ? vlans.Value! : Array.Empty<VlanInfo>()));
        }

        if (definition.Switches.Count > 0 && snapshots.Count == 0)
        {
            throw new DiscoveryStepException(
                DiscoveryErrorCodes.SwitchDiscoveryFailed,
                "All switches failed discovery.",
                retryable: anyRetryable);
        }

        return new SwitchDiscoveryOutcome(snapshots, definition.Switches.Count, failed);
    }

    /// <inheritdoc />
    public async Task<ServerDiscoveryOutcome> DiscoverServersAsync(
        RackDefinition definition, DeviceDiscoveryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var snapshots = new List<ServerNicSnapshot>();
        var failed = 0;
        var anyRetryable = false;

        foreach (var device in definition.Servers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_bmcRegistry.TryResolve(device.ToDescriptor(), out var factory))
            {
                failed++;
                LogDriverNotFound(context, device, DiscoveryStepName.BmcDiscovery);
                continue;
            }

            var driver = factory.Create(
                new BmcConnectionOptions(device.Host, device.Port, device.Timeout, device.CredentialsRef));

            var system = await InvokeAsync(
                context, device.DeviceKey, DiscoveryStepName.BmcDiscovery, "system-inventory",
                driver.GetSystemInventoryAsync, cancellationToken);
            var nics = await InvokeAsync(
                context, device.DeviceKey, DiscoveryStepName.BmcDiscovery, "network-interfaces",
                driver.GetNetworkInterfacesAsync, cancellationToken);

            var results = new IDriverOutcome[] { system, nics };
            if (results.All(r => !r.Success))
            {
                failed++;
                anyRetryable |= results.Any(r => r.Retryable);
                continue;
            }

            snapshots.Add(new ServerNicSnapshot(
                device.DeviceKey,
                system.Success ? system.Value : null,
                nics.Success ? nics.Value! : Array.Empty<BmcNetworkInterfaceInfo>()));
        }

        if (definition.Servers.Count > 0 && snapshots.Count == 0)
        {
            throw new DiscoveryStepException(
                DiscoveryErrorCodes.BmcDiscoveryFailed,
                "All servers failed discovery.",
                retryable: anyRetryable);
        }

        return new ServerDiscoveryOutcome(snapshots, definition.Servers.Count, failed);
    }

    private async Task<DriverCallOutcome<T>> InvokeAsync<T>(
        DeviceDiscoveryContext context,
        string deviceKey,
        DiscoveryStepName step,
        string operation,
        Func<CancellationToken, Task<DriverResult<T>>> call,
        CancellationToken cancellationToken)
    {
        var start = _time.GetTimestamp();
        try
        {
            var result = await call(cancellationToken);
            var durationMs = (long)_time.GetElapsedTime(start).TotalMilliseconds;
            var outcome = result.Success ? "success" : "failure";
#pragma warning disable CA2254 // structured template is constant; property set is fixed
            _logger.Log(
                result.Success ? LogLevel.Information : LogLevel.Warning,
                "Driver read-only call {Operation} on {DeviceKey} outcome={Outcome} " +
                "correlationId={CorrelationId} rackId={RackId} jobId={JobId} stepName={StepName} " +
                "durationMs={DurationMs} operationCategory={OperationCategory} errorCode={ErrorCode}",
                operation, deviceKey, outcome, context.CorrelationId, context.RackId, context.JobId,
                step, durationMs, ReadOnlyCategory, result.Error?.Code);
#pragma warning restore CA2254
            return new DriverCallOutcome<T>(result.Success, result.Value, result.Error?.Retryable ?? false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var durationMs = (long)_time.GetElapsedTime(start).TotalMilliseconds;
            _logger.LogWarning(
                ex,
                "Driver read-only call {Operation} on {DeviceKey} threw " +
                "correlationId={CorrelationId} rackId={RackId} jobId={JobId} stepName={StepName} " +
                "durationMs={DurationMs} operationCategory={OperationCategory}",
                operation, deviceKey, context.CorrelationId, context.RackId, context.JobId,
                step, durationMs, ReadOnlyCategory);
            return new DriverCallOutcome<T>(false, default, true);
        }
    }

    private void LogDriverNotFound(DeviceDiscoveryContext context, DeviceDefinition device, DiscoveryStepName step)
        => _logger.LogWarning(
            "No driver registered for {DeviceKey} vendor={Vendor} connectionKind={ConnectionKind} " +
            "correlationId={CorrelationId} rackId={RackId} jobId={JobId} stepName={StepName} " +
            "outcome=failure errorCode={ErrorCode} operationCategory={OperationCategory}",
            device.DeviceKey, device.Vendor, device.ConnectionKind, context.CorrelationId, context.RackId,
            context.JobId, step, DiscoveryErrorCodes.DriverNotFound, ReadOnlyCategory);

    /// <summary>Non-generic view of a driver call outcome so mixed-type results can be aggregated.</summary>
    private interface IDriverOutcome
    {
        bool Success { get; }

        bool Retryable { get; }
    }

    private sealed record DriverCallOutcome<T>(bool Success, T? Value, bool Retryable) : IDriverOutcome;
}
