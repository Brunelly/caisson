using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Caisson.Domain.Enums;
using Caisson.Drivers.Abstractions.Bmc;
using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.ReadOnly;
using Caisson.Drivers.Abstractions.Results;
using Caisson.Drivers.Redfish.Credentials;
using Caisson.Drivers.Redfish.Mapping;
using Caisson.Drivers.Redfish.Model;
using Caisson.Drivers.Redfish.Observability;
using Caisson.Drivers.Redfish.Serialization;
using Caisson.Drivers.Redfish.Transport;
using Microsoft.Extensions.Logging;

namespace Caisson.Drivers.Redfish;

/// <summary>
/// The HP iLO / Redfish read-only <see cref="IBmcDiscoveryDriver"/> with a per-method IPMI fallback
/// (story #5). Each method attempts Redfish first (HTTPS/JSON navigation through
/// <see cref="IRedfishClient"/>); on an unreachable/timeout/auth failure or structurally-insufficient data
/// it falls back to the read-only IPMI commands via <see cref="IIpmiCommandRunner"/>, recording the reason
/// and data-source provenance in diagnostics, a metric <c>source</c> tag and a secret-free log line —
/// without widening the shared Bmc records (ADR 0008/0009 precedent). Expected failures are converted to a
/// <see cref="DriverError"/> and never thrown; only caller cancellation surfaces as
/// <see cref="OperationCanceledException"/> (ADR 0006). A single linked <see cref="CancellationTokenSource"/>
/// enforces the per-call timeout as the overall budget across both Redfish navigation and IPMI fallback.
/// </summary>
public sealed class RedfishBmcDriver : IBmcDiscoveryDriver
{
    /// <summary>The identity this driver and its factory report (vendor HPE, generic iLO line, Redfish kind).</summary>
    public static readonly DriverDescriptor RedfishDescriptor =
        new("HPE", null, DriverConnectionKind.Redfish, "1.0.0");

    private readonly string _host;
    private readonly Func<IRedfishClient> _redfishClientFactory;
    private readonly Func<IIpmiCommandRunner> _ipmiRunnerFactory;
    private readonly Func<IpmiConnectionSettings> _ipmiSettingsFactory;
    private readonly TimeSpan _budget;
    private readonly RedfishMetrics _metrics;
    private readonly ILogger<RedfishBmcDriver> _logger;

    /// <summary>Creates a driver bound to <paramref name="host"/> with lazy per-connection client/runner factories.</summary>
    public RedfishBmcDriver(
        string host,
        Func<IRedfishClient> redfishClientFactory,
        Func<IIpmiCommandRunner> ipmiRunnerFactory,
        Func<IpmiConnectionSettings> ipmiSettingsFactory,
        TimeSpan budget,
        RedfishMetrics metrics,
        ILogger<RedfishBmcDriver> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(redfishClientFactory);
        ArgumentNullException.ThrowIfNull(ipmiRunnerFactory);
        ArgumentNullException.ThrowIfNull(ipmiSettingsFactory);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _host = host;
        _redfishClientFactory = redfishClientFactory;
        _ipmiRunnerFactory = ipmiRunnerFactory;
        _ipmiSettingsFactory = ipmiSettingsFactory;
        _budget = budget > TimeSpan.Zero ? budget : TimeSpan.FromSeconds(10);
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public DriverDescriptor Descriptor => RedfishDescriptor;

    /// <inheritdoc />
    public Task<DriverResult<BmcSystemInventory>> GetSystemInventoryAsync(CancellationToken cancellationToken)
        => ExecuteAsync<BmcSystemInventory>("systemInventory", cancellationToken, (budget, diagnostics) =>
            RedfishFirstAsync(
                "system inventory", budget, diagnostics,
                viaRedfish: async (client, ct) =>
                {
                    var system = await GetComputerSystemAsync(client, ct).ConfigureAwait(false);
                    var inventory = RedfishMappers.MapSystemInventory(system, _host, diagnostics);
                    // Sufficient once a ComputerSystem was found; a degraded identity is a warning, not a fallback.
                    return (inventory, Sufficient: system is not null);
                },
                viaIpmi: async ct =>
                {
                    var mcInfo = await RunIpmiRecordAsync(IpmiReadCommands.McInfo, "mc info", diagnostics, ct).ConfigureAwait(false);
                    var fru = await RunIpmiRecordAsync(IpmiReadCommands.FruPrint, "fru print", diagnostics, ct).ConfigureAwait(false);
                    if (mcInfo is null && fru is null)
                    {
                        return (false, default!);
                    }

                    var inventory = IpmiOutputParser.MapSystemInventory(
                        mcInfo ?? Empty(), fru ?? Empty(), _host, diagnostics);
                    return (true, inventory);
                }));

    /// <inheritdoc />
    public Task<DriverResult<IReadOnlyList<BmcNetworkInterfaceInfo>>> GetNetworkInterfacesAsync(
        CancellationToken cancellationToken)
        => ExecuteAsync<IReadOnlyList<BmcNetworkInterfaceInfo>>("networkInterfaces", cancellationToken, (budget, diagnostics) =>
            RedfishFirstAsync(
                "network interfaces", budget, diagnostics,
                viaRedfish: async (client, ct) =>
                {
                    var nics = await GetNetworkInterfacesViaRedfishAsync(client, diagnostics, ct).ConfigureAwait(false);
                    // Insufficient when there is no NIC at all or not one carries a usable MAC — the exact
                    // structural gap that warrants the IPMI fallback (AC2/AC3).
                    var sufficient = nics.Count > 0 && nics.Any(n => n.Mac is not null);
                    return (nics, sufficient);
                },
                viaIpmi: async ct =>
                {
                    var lan = await RunIpmiRecordAsync(IpmiReadCommands.LanPrint, "lan print", diagnostics, ct).ConfigureAwait(false);
                    if (lan is null)
                    {
                        return (false, default!);
                    }

                    var nics = IpmiOutputParser.MapNetworkInterfaces(lan, diagnostics);
                    return (true, nics);
                }));

    /// <inheritdoc />
    public Task<DriverResult<BmcBiosInfo>> GetBiosInfoAsync(CancellationToken cancellationToken)
        => ExecuteAsync<BmcBiosInfo>("biosInfo", cancellationToken, (budget, diagnostics) =>
            RedfishFirstAsync(
                "BIOS info", budget, diagnostics,
                viaRedfish: async (client, ct) =>
                {
                    var system = await GetComputerSystemAsync(client, ct).ConfigureAwait(false);
                    var bios = RedfishMappers.MapBiosInfo(system, diagnostics);
                    return (bios, Sufficient: system is not null);
                },
                viaIpmi: async ct =>
                {
                    var mcInfo = await RunIpmiRecordAsync(IpmiReadCommands.McInfo, "mc info", diagnostics, ct).ConfigureAwait(false);
                    var fru = await RunIpmiRecordAsync(IpmiReadCommands.FruPrint, "fru print", diagnostics, ct).ConfigureAwait(false);
                    if (mcInfo is null && fru is null)
                    {
                        return (false, default!);
                    }

                    var bios = IpmiOutputParser.MapBiosInfo(mcInfo ?? Empty(), fru ?? Empty(), diagnostics);
                    return (true, bios);
                }));

    // --- Redfish navigation ---

    private async Task<ComputerSystem?> GetComputerSystemAsync(IRedfishClient client, CancellationToken cancellationToken)
    {
        var root = await GetJsonAsync(client, RedfishReadPaths.ServiceRoot, RedfishJsonContext.Default.ServiceRoot, cancellationToken).ConfigureAwait(false);
        var systemsLink = root?.Systems?.Id ?? RedfishReadPaths.Systems;

        var collection = await GetJsonAsync(client, systemsLink, RedfishJsonContext.Default.RedfishCollection, cancellationToken).ConfigureAwait(false);
        var memberLink = RedfishMappers.MemberLinks(collection).FirstOrDefault();
        if (memberLink is null)
        {
            return null;
        }

        return await GetJsonAsync(client, memberLink, RedfishJsonContext.Default.ComputerSystem, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<BmcNetworkInterfaceInfo>> GetNetworkInterfacesViaRedfishAsync(
        IRedfishClient client, List<DriverDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var system = await GetComputerSystemAsync(client, cancellationToken).ConfigureAwait(false);
        var ethLink = system?.EthernetInterfaces?.Id;
        if (ethLink is null)
        {
            return Array.Empty<BmcNetworkInterfaceInfo>();
        }

        var collection = await GetJsonAsync(client, ethLink, RedfishJsonContext.Default.RedfishCollection, cancellationToken).ConfigureAwait(false);
        var links = RedfishMappers.MemberLinks(collection);

        var interfaces = new List<EthernetInterface?>(links.Count);
        foreach (var link in links)
        {
            interfaces.Add(await GetJsonAsync(client, link, RedfishJsonContext.Default.EthernetInterface, cancellationToken).ConfigureAwait(false));
        }

        return RedfishMappers.MapNetworkInterfaces(interfaces, diagnostics);
    }

    private static async Task<T?> GetJsonAsync<T>(
        IRedfishClient client, string path, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        where T : class
    {
        var body = await client.GetAsync(path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(body, typeInfo);
    }

    // --- IPMI fallback ---

    private async Task<IpmiRecord?> RunIpmiRecordAsync(
        IReadOnlyList<string> argv, string section, List<DriverDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        IpmiConnectionSettings settings;
        try
        {
            settings = _ipmiSettingsFactory();
        }
        catch (BmcCredentialResolutionException)
        {
            // No IPMI credentials — treat as "no data from this command"; the caller decides overall success.
            return null;
        }

        var result = await _ipmiRunnerFactory().RunAsync(argv, settings, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            diagnostics.Add(new DriverDiagnostic(
                DriverDiagnosticSeverity.Warning, ReasonCode.DeviceUnreachable, section,
                result.Available
                    ? $"The IPMI '{section}' command exited with code {result.ExitCode}."
                    : "ipmitool is not available; the IPMI fallback could not run."));
            return null;
        }

        return IpmiOutputParser.Parse(result.StandardOutput, section, diagnostics);
    }

    private static IpmiRecord Empty() => new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    // --- Redfish-first-per-method envelope ---

    private async Task<(T Value, string Source)> RedfishFirstAsync<T>(
        string section,
        CancellationToken budget,
        List<DriverDiagnostic> diagnostics,
        Func<IRedfishClient, CancellationToken, Task<(T Value, bool Sufficient)>> viaRedfish,
        Func<CancellationToken, Task<(bool Ok, T Value)>> viaIpmi)
        where T : notnull
    {
        T redfishValue = default!;
        var haveRedfish = false;
        string? failureReason = null;
        Exception? redfishError = null;

        var client = _redfishClientFactory();
        try
        {
            var (value, sufficient) = await viaRedfish(client, budget).ConfigureAwait(false);
            redfishValue = value;
            haveRedfish = true;
            if (sufficient)
            {
                return (value, RedfishMetrics.SourceRedfish);
            }

            failureReason = "Redfish returned insufficient data (missing or MAC-less inventory).";
        }
        catch (Exception ex) when (!budget.IsCancellationRequested && IsRedfishFallbackEligible(ex))
        {
            redfishError = ex;
            failureReason = DescribeFailure(ex);
        }
        finally
        {
            client.Dispose();
        }

        // Redfish was unreachable or insufficient — record the reason and attempt the read-only IPMI fallback.
        diagnostics.Add(new DriverDiagnostic(
            DriverDiagnosticSeverity.Warning, ReasonCode.DeviceUnreachable, section,
            $"Redfish could not fully satisfy '{section}': {failureReason} Attempting IPMI fallback."));
        _logger.LogWarning(
            "Redfish {Section} for {Host} degraded ({Reason}); falling back to IPMI.", section, _host, failureReason);

        var (ipmiOk, ipmiValue) = await viaIpmi(budget).ConfigureAwait(false);
        if (ipmiOk)
        {
            diagnostics.Add(new DriverDiagnostic(
                DriverDiagnosticSeverity.Warning, ReasonCode.FallbackSource, section,
                $"'{section}' was sourced from the IPMI fallback because Redfish was unavailable or insufficient."));
            _logger.LogInformation(
                "Redfish {Section} for {Host} was sourced from the IPMI fallback.", section, _host);
            return (ipmiValue, RedfishMetrics.SourceIpmi);
        }

        if (haveRedfish)
        {
            // IPMI could not improve on it — keep the (degraded) Redfish data rather than failing the call.
            return (redfishValue, RedfishMetrics.SourceRedfish);
        }

        // Neither path yielded data — surface the original Redfish failure so ExecuteAsync maps it faithfully.
        throw redfishError
            ?? new RedfishException($"Neither Redfish nor IPMI could read '{section}' from the BMC.");
    }

    private async Task<DriverResult<T>> ExecuteAsync<T>(
        string query,
        CancellationToken cancellationToken,
        Func<CancellationToken, List<DriverDiagnostic>, Task<(T Value, string Source)>> body)
        where T : notnull
    {
        // Cancellation is caller-initiated control flow — let it propagate before we do any work.
        cancellationToken.ThrowIfCancellationRequested();

        var correlationId = Activity.Current?.Id ?? Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["BmcHost"] = _host,
            ["Driver"] = "redfish",
            ["Query"] = query,
        });

        // A single linked CTS is the overall per-call budget, shared across the multi-GET Redfish navigation
        // AND any IPMI fallback within this call (treats the configured Timeout as the P95<=10s budget).
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_budget);

        var stopwatch = Stopwatch.StartNew();
        var diagnostics = new List<DriverDiagnostic>();
        var source = RedfishMetrics.SourceRedfish;
        try
        {
            var (value, usedSource) = await body(budget.Token, diagnostics).ConfigureAwait(false);
            source = usedSource;
            stopwatch.Stop();
            _metrics.RecordSuccess(query, source, stopwatch.Elapsed);
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
            _metrics.RecordFailure(query, source, stopwatch.Elapsed);
            _logger.LogWarning(
                "Redfish {Query} failed for {Host}: {Code} (retryable={Retryable})",
                query, _host, error.Code, error.Retryable);
            return DriverResult<T>.Fail(error, stopwatch.Elapsed, diagnostics.Count > 0 ? diagnostics : null);
        }
    }

    private static bool IsRedfishFallbackEligible(Exception ex) => ex is
        HttpRequestException or SocketException or TimeoutException
        or RedfishException or JsonException or IOException;

    private static string DescribeFailure(Exception ex) => ex switch
    {
        RedfishAuthenticationException => "the BMC rejected the credentials",
        TimeoutException => "the request timed out",
        HttpRequestException or SocketException => "the endpoint was unreachable",
        JsonException => "the response could not be parsed",
        _ => "the Redfish request failed",
    };

    private static DriverError MapError(Exception exception) => exception switch
    {
        RedfishAuthenticationException => new DriverError(
            DriverErrorCode.AuthenticationFailed, "The BMC rejected the supplied credentials (IPMI_AUTH_FAILED).", Retryable: false),
        BmcCredentialResolutionException => new DriverError(
            DriverErrorCode.AuthenticationFailed, "BMC credentials could not be resolved.", Retryable: false),
        TimeoutException => new DriverError(
            DriverErrorCode.ConnectionTimeout, "The BMC did not respond within the timeout (REDFISH_TIMEOUT).", Retryable: true),
        OperationCanceledException => new DriverError(
            DriverErrorCode.ConnectionTimeout, "BMC discovery exceeded its time budget (REDFISH_TIMEOUT).", Retryable: true),
        HttpRequestException http => MapHttp(http),
        SocketException socket => MapSocket(socket),
        System.Security.Authentication.AuthenticationException => new DriverError(
            DriverErrorCode.ProtocolError, "The BMC's TLS certificate was not trusted.", Retryable: false),
        JsonException => new DriverError(
            DriverErrorCode.ParseError, "A Redfish response could not be parsed (REDFISH_SCHEMA_MISSING_FIELD).", Retryable: false),
        RedfishException => new DriverError(
            DriverErrorCode.ParseError, "The BMC returned an unexpected Redfish response (REDFISH_SCHEMA_MISSING_FIELD).", Retryable: false),
        _ => new DriverError(
            DriverErrorCode.Unknown, "An unexpected error occurred communicating with the BMC.", Retryable: false),
    };

    private static DriverError MapHttp(HttpRequestException http) => http.InnerException switch
    {
        SocketException socket => MapSocket(socket),
        _ => new DriverError(
            DriverErrorCode.DeviceUnreachable, "The BMC could not be reached.", Retryable: true),
    };

    private static DriverError MapSocket(SocketException socket) => socket.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => new DriverError(
            DriverErrorCode.ConnectionRefused, "The BMC refused the connection.", Retryable: true),
        SocketError.TimedOut => new DriverError(
            DriverErrorCode.ConnectionTimeout, "Connecting to the BMC timed out.", Retryable: true),
        _ => new DriverError(
            DriverErrorCode.DeviceUnreachable, "The BMC could not be reached.", Retryable: true),
    };
}
