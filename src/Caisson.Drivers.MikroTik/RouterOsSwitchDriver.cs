using System.Diagnostics;
using System.Net.Sockets;
using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.ReadOnly;
using Caisson.Drivers.Abstractions.Results;
using Caisson.Drivers.Abstractions.Switches;
using Caisson.Drivers.MikroTik.Credentials;
using Caisson.Drivers.MikroTik.Mapping;
using Caisson.Drivers.MikroTik.Observability;
using Caisson.Drivers.MikroTik.Transport;
using Microsoft.Extensions.Logging;

namespace Caisson.Drivers.MikroTik;

/// <summary>
/// The MikroTik RouterOS read-only <see cref="ISwitchDiscoveryDriver"/> (story #4). Each method opens
/// its own connection, runs its <c>print</c> query/queries through <see cref="IRouterOsApiClient"/>,
/// maps the raw rows into the story-3 Switches info records, and returns a <see cref="DriverResult{T}"/>.
/// Expected failures (unreachable, auth, timeout, protocol/parse) are converted to a
/// <see cref="DriverError"/> — never thrown — so one section failing never blocks the others; only
/// caller cancellation surfaces as <see cref="OperationCanceledException"/> (ADR 0006).
/// </summary>
public sealed class RouterOsSwitchDriver : ISwitchDiscoveryDriver
{
    /// <summary>The identity this driver and its factory report.</summary>
    public static readonly DriverDescriptor RouterOsDescriptor =
        new("MikroTik", null, DriverConnectionKind.RouterOsApi, "1.0.0");

    private readonly string _host;
    private readonly Func<IRouterOsApiClient> _clientFactory;
    private readonly TimeSpan _budget;
    private readonly RouterOsMetrics _metrics;
    private readonly ILogger<RouterOsSwitchDriver> _logger;

    /// <summary>Creates a driver bound to <paramref name="host"/> that builds a fresh client per call via <paramref name="clientFactory"/>.</summary>
    public RouterOsSwitchDriver(
        string host,
        Func<IRouterOsApiClient> clientFactory,
        TimeSpan budget,
        RouterOsMetrics metrics,
        ILogger<RouterOsSwitchDriver> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _host = host;
        _clientFactory = clientFactory;
        _budget = budget > TimeSpan.Zero ? budget : TimeSpan.FromSeconds(10);
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public DriverDescriptor Descriptor => RouterOsDescriptor;

    /// <inheritdoc />
    public Task<DriverResult<SwitchDeviceInfo>> GetDeviceInfoAsync(CancellationToken cancellationToken)
        => ExecuteAsync("deviceInfo", cancellationToken, async (client, diagnostics, ct) =>
        {
            var resourceRows = await client.SendCommandAsync(RouterOsReadCommands.SystemResource, ct).ConfigureAwait(false);
            var routerboardRows = await TryReadAsync(client, RouterOsReadCommands.SystemRouterboard, "routerboard", diagnostics, ct).ConfigureAwait(false);

            var resource = new RouterOsRecordOrEmpty(resourceRows);
            var routerboard = routerboardRows.Count > 0 ? new Parsing.RouterOsRecord(routerboardRows[0]) : null;
            return RouterOsMappers.MapDeviceInfo(resource.Record, routerboard, _host);
        });

    /// <inheritdoc />
    public Task<DriverResult<IReadOnlyList<SwitchPortInfo>>> GetPortsAsync(CancellationToken cancellationToken)
        => ExecuteAsync<IReadOnlyList<SwitchPortInfo>>("interfaces", cancellationToken, async (client, diagnostics, ct) =>
        {
            var interfaceRows = await client.SendCommandAsync(RouterOsReadCommands.Interfaces, ct).ConfigureAwait(false);
            var ethernetRows = await TryReadAsync(client, RouterOsReadCommands.EthernetInterfaces, "ethernet", diagnostics, ct).ConfigureAwait(false);
            var bridgePortRows = await TryReadAsync(client, RouterOsReadCommands.BridgePorts, "bridge-port", diagnostics, ct).ConfigureAwait(false);
            var bridgeVlanRows = await TryReadAsync(client, RouterOsReadCommands.BridgeVlans, "bridge-vlan", diagnostics, ct).ConfigureAwait(false);

            return RouterOsMappers.MapPorts(interfaceRows, ethernetRows, bridgePortRows, bridgeVlanRows, diagnostics);
        });

    /// <inheritdoc />
    public Task<DriverResult<IReadOnlyList<LldpNeighbourInfo>>> GetLldpNeighborsAsync(CancellationToken cancellationToken)
        => ExecuteAsync<IReadOnlyList<LldpNeighbourInfo>>("lldp", cancellationToken, async (client, diagnostics, ct) =>
        {
            var neighbourRows = await client.SendCommandAsync(RouterOsReadCommands.IpNeighbors, ct).ConfigureAwait(false);
            return RouterOsMappers.MapLldpNeighbours(neighbourRows, diagnostics);
        });

    /// <inheritdoc />
    public Task<DriverResult<IReadOnlyList<BridgeHostEntry>>> GetBridgeHostTableAsync(CancellationToken cancellationToken)
        => ExecuteAsync<IReadOnlyList<BridgeHostEntry>>("bridgeHost", cancellationToken, async (client, diagnostics, ct) =>
        {
            var hostRows = await client.SendCommandAsync(RouterOsReadCommands.BridgeHosts, ct).ConfigureAwait(false);
            return RouterOsMappers.MapBridgeHosts(hostRows, diagnostics);
        });

    /// <inheritdoc />
    public Task<DriverResult<IReadOnlyList<VlanInfo>>> GetVlansAsync(CancellationToken cancellationToken)
        => ExecuteAsync<IReadOnlyList<VlanInfo>>("bridgeVlan", cancellationToken, async (client, diagnostics, ct) =>
        {
            var bridgeVlanRows = await TryReadAsync(client, RouterOsReadCommands.BridgeVlans, "bridge-vlan", diagnostics, ct).ConfigureAwait(false);
            var vlanInterfaceRows = await TryReadAsync(client, RouterOsReadCommands.VlanInterfaces, "vlan-interface", diagnostics, ct).ConfigureAwait(false);

            return RouterOsMappers.MapVlans(bridgeVlanRows, vlanInterfaceRows, diagnostics);
        });

    private async Task<DriverResult<T>> ExecuteAsync<T>(
        string query,
        CancellationToken cancellationToken,
        Func<IRouterOsApiClient, List<DriverDiagnostic>, CancellationToken, Task<T>> body)
        where T : notnull
    {
        // Cancellation is caller-initiated control flow — let it propagate before we do any work.
        cancellationToken.ThrowIfCancellationRequested();

        var correlationId = Activity.Current?.Id ?? Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["SwitchHost"] = _host,
            ["Driver"] = "routeros",
            ["Query"] = query,
        });

        // A single linked CTS is the overall per-call budget, shared across every sub-command this query
        // issues (e.g. GetPortsAsync's four commands), mirroring RedfishBmcDriver.ExecuteAsync. Without
        // this, each sub-command's own per-command timeout (applied inside RouterOsApiClient) gave a
        // multi-command query one full timeout window PER command instead of one overall budget.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_budget);

        var stopwatch = Stopwatch.StartNew();
        var diagnostics = new List<DriverDiagnostic>();
        IRouterOsApiClient? client = null;
        try
        {
            client = _clientFactory();
            await client.ConnectAsync(budget.Token).ConfigureAwait(false);
            var value = await body(client, diagnostics, budget.Token).ConfigureAwait(false);

            stopwatch.Stop();
            _metrics.RecordSuccess(query, stopwatch.Elapsed);
            return DriverResult<T>.Ok(value, stopwatch.Elapsed, diagnostics.Count > 0 ? diagnostics : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var error = MapError(ex);
            _metrics.RecordFailure(query, stopwatch.Elapsed);
            _logger.LogWarning(
                "RouterOS {Query} failed for {Host}: {Code} (retryable={Retryable})",
                query, _host, error.Code, error.Retryable);
            return DriverResult<T>.Fail(error, stopwatch.Elapsed, diagnostics.Count > 0 ? diagnostics : null);
        }
        finally
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Reads an auxiliary section, converting any failure to a per-section diagnostic and an empty set
    /// so a missing/erroring auxiliary query (e.g. bridge VLANs on a device without VLAN filtering)
    /// degrades the result rather than failing the whole call (AC3). Caller cancellation still propagates.
    /// </summary>
    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> TryReadAsync(
        IRouterOsApiClient client,
        string command,
        string section,
        List<DriverDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            diagnostics.Add(new DriverDiagnostic(
                DriverDiagnosticSeverity.Warning,
                Caisson.Domain.Enums.ReasonCode.DeviceUnreachable,
                section,
                $"The '{section}' section could not be read and was skipped."));
            return Array.Empty<IReadOnlyDictionary<string, string>>();
        }
    }

    private static DriverError MapError(Exception exception) => exception switch
    {
        RouterOsAuthenticationException => new DriverError(
            DriverErrorCode.AuthenticationFailed, "Authentication with the RouterOS device failed.", Retryable: false),
        CredentialResolutionException => new DriverError(
            DriverErrorCode.AuthenticationFailed, "RouterOS credentials could not be resolved.", Retryable: false),
        TimeoutException => new DriverError(
            DriverErrorCode.ConnectionTimeout, "The RouterOS device did not respond within the timeout.", Retryable: true),
        OperationCanceledException => new DriverError(
            DriverErrorCode.ConnectionTimeout, "RouterOS discovery exceeded its overall time budget.", Retryable: true),
        SocketException socket => socket.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => new DriverError(
                DriverErrorCode.ConnectionRefused, "The RouterOS device refused the connection.", Retryable: true),
            SocketError.TimedOut => new DriverError(
                DriverErrorCode.ConnectionTimeout, "Connecting to the RouterOS device timed out.", Retryable: true),
            _ => new DriverError(
                DriverErrorCode.DeviceUnreachable, "The RouterOS device could not be reached.", Retryable: true),
        },
        System.Security.Authentication.AuthenticationException => new DriverError(
            DriverErrorCode.ProtocolError, "The RouterOS device's TLS certificate was not trusted.", Retryable: false),
        EndOfStreamException => new DriverError(
            DriverErrorCode.DeviceUnreachable, "The RouterOS connection closed unexpectedly.", Retryable: true),
        FormatException => new DriverError(
            DriverErrorCode.ParseError, "A RouterOS response could not be parsed.", Retryable: false),
        RouterOsApiException => new DriverError(
            DriverErrorCode.ProtocolError, "The RouterOS device returned an unexpected protocol response.", Retryable: false),
        _ => new DriverError(
            DriverErrorCode.Unknown, "An unexpected error occurred communicating with the RouterOS device.", Retryable: false),
    };

    /// <summary>Wraps the first resource row, or an empty record when the device returned none.</summary>
    private readonly struct RouterOsRecordOrEmpty
    {
        public RouterOsRecordOrEmpty(IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
            => Record = new Parsing.RouterOsRecord(
                rows.Count > 0 ? rows[0] : new Dictionary<string, string>());

        public Parsing.RouterOsRecord Record { get; }
    }
}
