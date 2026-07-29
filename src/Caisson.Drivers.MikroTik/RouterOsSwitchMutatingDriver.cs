using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.Mutating;
using Caisson.Drivers.Abstractions.Results;
using Caisson.Drivers.MikroTik.Credentials;
using Caisson.Drivers.MikroTik.Mapping;
using Caisson.Drivers.MikroTik.Observability;
using Caisson.Drivers.MikroTik.Parsing;
using Caisson.Drivers.MikroTik.Transport;
using Microsoft.Extensions.Logging;

namespace Caisson.Drivers.MikroTik;

/// <summary>
/// The MikroTik RouterOS write-capable <see cref="ISwitchMutatingDriver"/> (ADR 0031). Implements the
/// single bounded operation — set a port's access VLAN — as validate → read-before → idempotency
/// short-circuit → dry-run preview → arm-rollback → apply → verify → confirm. The confirmed-commit
/// mechanism is a device-side one-shot <c>/system/scheduler</c> job armed BEFORE the change is applied
/// and removed only after a successful post-change verification; anything that prevents confirmation
/// (a crash, an unconfirmed caller, a verification mismatch) leaves the device to self-revert once the
/// window elapses, so the management path can never be severed irrecoverably.
/// </summary>
public sealed partial class RouterOsSwitchMutatingDriver : ISwitchMutatingDriver
{
    /// <summary>The identity this driver and its factory report.</summary>
    public static readonly DriverDescriptor RouterOsMutatingDescriptor =
        new("MikroTik", null, DriverConnectionKind.RouterOsApi, "1.0.0");

    private const int MinVlanId = 1;
    private const int MaxVlanId = 4094;

    /// <summary>RouterOS's own default bridge-port PVID when a port has never had one explicitly set.</summary>
    private const int DefaultPvid = 1;

    /// <summary>
    /// The fixed, driver-owned rollback script template. Only the port name (charset-validated by
    /// <see cref="PortNamePattern"/> before this is ever built) and the already-observed before-PVID
    /// (an int read from the device) are substituted — never caller free-text — so the confirmed-commit
    /// job can never become a command-injection surface (NFR1).
    /// </summary>
    private const string RollbackScriptTemplate =
        "/interface/bridge/port/set [find interface=\"{0}\"] pvid={1}";

    private static readonly SwitchChangePlan EmptyPlan = new(Array.Empty<SwitchChangeStep>());
    private static readonly char[] ListSeparators = { ',', ' ', ';' };

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.:-]{0,62}$")]
    private static partial Regex PortNamePattern();

    private readonly string _host;
    private readonly Func<IRouterOsWriteApiClient> _clientFactory;
    private readonly TimeSpan _budget;
    private readonly TimeSpan _defaultConfirmWindow;
    private readonly RouterOsWriteMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RouterOsSwitchMutatingDriver> _logger;

    /// <summary>Creates a driver bound to <paramref name="host"/> that builds a fresh client per call via <paramref name="clientFactory"/>.</summary>
    public RouterOsSwitchMutatingDriver(
        string host,
        Func<IRouterOsWriteApiClient> clientFactory,
        TimeSpan budget,
        TimeSpan defaultConfirmWindow,
        RouterOsWriteMetrics metrics,
        TimeProvider timeProvider,
        ILogger<RouterOsSwitchMutatingDriver> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _host = host;
        _clientFactory = clientFactory;
        _budget = budget > TimeSpan.Zero ? budget : TimeSpan.FromSeconds(10);
        _defaultConfirmWindow = defaultConfirmWindow > TimeSpan.Zero
            ? defaultConfirmWindow
            : SwitchMutatingConnectionOptions.DefaultConfirmWindow;
        _metrics = metrics;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public DriverDescriptor Descriptor => RouterOsMutatingDescriptor;

    /// <inheritdoc />
    public async Task<DriverResult<SetAccessVlanOutcome>> SetAccessVlanAsync(
        SetAccessVlanRequest request, CancellationToken cancellationToken)
    {
        var pending = await BeginChangeAsync(request, cancellationToken).ConfigureAwait(false);
        return await ConfirmChangeAsync(pending, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Test seam (mirrors the codebase's <c>internal RouterOsApiClient(...)</c> convention): runs
    /// everything up to and including apply+verify, but stops short of sending the confirm signal. Lets
    /// tests exercise the real auto-rollback path by never calling <see cref="ConfirmChangeAsync"/>.
    /// <see cref="SetAccessVlanAsync"/> composes this with <see cref="ConfirmChangeAsync"/>.
    /// </summary>
    internal async Task<PendingChange> BeginChangeAsync(SetAccessVlanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var confirmWindow = request.ConfirmWindow is { } requested && requested > TimeSpan.Zero
            ? requested
            : _defaultConfirmWindow;

        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = request.CorrelationId,
            ["SwitchHost"] = _host,
            ["Driver"] = "routeros",
            ["Port"] = request.PortName,
            ["VlanId"] = request.DesiredVlanId,
            ["DryRun"] = request.DryRun,
            ["ConfirmWindowSeconds"] = confirmWindow.TotalSeconds,
        });

        var stopwatch = Stopwatch.StartNew();

        // 1. Validate the VLAN id BEFORE any I/O (NFR1/AC3) — an out-of-range id never reaches the device.
        if (request.DesiredVlanId is < MinVlanId or > MaxVlanId)
        {
            return Finish(request, confirmWindow, EmptyPlan, null, null, null, false,
                SwitchChangeReasonCode.InvalidVlanId, stopwatch);
        }

        // The port name is about to be embedded in a scheduler on-event script (the rollback template) —
        // validate its charset first so it can never carry script-injection content.
        if (!PortNamePattern().IsMatch(request.PortName))
        {
            return Finish(request, confirmWindow, EmptyPlan, null, null, null, false,
                SwitchChangeReasonCode.PortNotFound, stopwatch);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_budget);

        IRouterOsWriteApiClient? client = null;
        try
        {
            client = _clientFactory();
            await client.ConnectAsync(budget.Token).ConfigureAwait(false);

            // 2. Read before-state: the port's PVID plus the minimal bridge-VLAN untagged-membership
            // subset needed to assert access-VLAN semantics (the story's answered question).
            var portRows = await client.ExecuteAsync(
                RouterOsWriteCommands.BridgePortPrint, new[] { "?interface=" + request.PortName }, budget.Token)
                .ConfigureAwait(false);

            if (portRows.Count == 0)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                return Finish(request, confirmWindow, EmptyPlan, null, null, null, false,
                    SwitchChangeReasonCode.PortNotFound, stopwatch);
            }

            if (portRows.Count > 1)
            {
                // Fail fast on ambiguity rather than guessing which row is authoritative.
                await client.DisposeAsync().ConfigureAwait(false);
                return Finish(request, confirmWindow, EmptyPlan, null, null, null, false,
                    SwitchChangeReasonCode.AmbiguousPort, stopwatch);
            }

            var portRecord = new RouterOsRecord(portRows[0]);
            var portId = portRecord.GetString(".id");
            var currentPvid = portRecord.GetInt("pvid") ?? DefaultPvid;

            var bridgeVlanRows = await client.ExecuteAsync(
                RouterOsWriteCommands.BridgeVlanPrint, Array.Empty<string>(), budget.Token).ConfigureAwait(false);

            var before = new SwitchAccessVlanState(
                request.PortName, currentPvid, ExtractUntaggedVlanIds(bridgeVlanRows, request.PortName));

            if (!IsVlanConfigured(bridgeVlanRows, request.DesiredVlanId))
            {
                // Requiring the VLAN to already exist (rather than auto-creating it) keeps the write
                // surface minimal per NFR1 and the story's out-of-scope list.
                await client.DisposeAsync().ConfigureAwait(false);
                return Finish(request, confirmWindow, EmptyPlan, before, null, null, false,
                    SwitchChangeReasonCode.VlanNotConfigured, stopwatch);
            }

            // 3. Idempotency short-circuit (AC2/NFR3): no scheduler entry, no /set.
            if (before.Pvid == request.DesiredVlanId)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                return Finish(request, confirmWindow, EmptyPlan, before, before, null, false,
                    SwitchChangeReasonCode.NoOpAlreadyDesiredState, stopwatch);
            }

            var plan = new SwitchChangePlan(new SwitchChangeStep[]
            {
                new BridgePortPvidChange(request.PortName, before.Pvid ?? DefaultPvid, request.DesiredVlanId),
                new ConfirmedCommitWindowArmed(confirmWindow),
            });

            // 4. Dry-run: return the plan and an intended preview. Zero writes.
            if (request.DryRun)
            {
                var intendedAfter = before with { Pvid = request.DesiredVlanId };
                await client.DisposeAsync().ConfigureAwait(false);
                return Finish(request, confirmWindow, plan, before, intendedAfter, null, false,
                    SwitchChangeReasonCode.DryRunPlanned, stopwatch);
            }

            if (portId is null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw new RouterOsApiException("RouterOS bridge port row had no '.id' attribute.");
            }

            // 5. Arm the confirmed-commit rollback FIRST, before applying anything — the device can never
            // end up changed with no self-revert armed (ADR 0031, the "can't brick the un-bricker" rule).
            var entryName = BuildSchedulerEntryName(request.CorrelationId);
            var rollbackScript = string.Format(
                CultureInfo.InvariantCulture, RollbackScriptTemplate, request.PortName, before.Pvid ?? DefaultPvid);
            var windowSeconds = Math.Max(1, (int)Math.Ceiling(confirmWindow.TotalSeconds));

            await client.ExecuteAsync(RouterOsWriteCommands.SchedulerAdd, new[]
            {
                "=name=" + entryName,
                "=on-event=" + rollbackScript,
                "=start-time=+" + windowSeconds.ToString(CultureInfo.InvariantCulture) + "s",
                "=interval=00:00:00",
            }, budget.Token).ConfigureAwait(false);

            try
            {
                // 6. Apply, then verify by reading the port back.
                await client.ExecuteAsync(RouterOsWriteCommands.BridgePortSet, new[]
                {
                    "=.id=" + portId,
                    "=pvid=" + request.DesiredVlanId.ToString(CultureInfo.InvariantCulture),
                }, budget.Token).ConfigureAwait(false);

                var verifyRows = await client.ExecuteAsync(
                    RouterOsWriteCommands.BridgePortPrint, new[] { "?interface=" + request.PortName }, budget.Token)
                    .ConfigureAwait(false);
                var observedPvid = verifyRows.Count == 1 ? new RouterOsRecord(verifyRows[0]).GetInt("pvid") : null;
                var verified = observedPvid == request.DesiredVlanId;

                var verification = new VerificationResult(
                    verified, request.DesiredVlanId, observedPvid,
                    verified
                        ? null
                        : $"Expected PVID {request.DesiredVlanId} but observed " +
                          $"{(observedPvid?.ToString(CultureInfo.InvariantCulture) ?? "none")}.");

                var after = before with { Pvid = observedPvid ?? before.Pvid };

                if (!verified)
                {
                    // Do not confirm — the armed scheduler entry self-reverts once the window elapses.
                    await client.DisposeAsync().ConfigureAwait(false);
                    return Finish(request, confirmWindow, plan, before, after, verification, false,
                        SwitchChangeReasonCode.VerificationFailed, stopwatch);
                }

                // Verified: hand back an open client plus the armed entry name so ConfirmChangeAsync can
                // send the confirm signal. Confirmed stays false until that succeeds.
                stopwatch.Stop();
                var appliedResult = BuildResult(request, confirmWindow, plan, before, after, verification,
                    confirmed: false, SwitchChangeReasonCode.Applied, stopwatch.Elapsed);

                return new PendingChange(appliedResult, client, entryName);
            }
            catch
            {
                // Applying or verifying threw — best-effort cancel the armed rollback so a genuinely
                // transient failure (e.g. a dropped connection right after /set never took effect) doesn't
                // leave a dangling entry. If this also fails, the armed window still self-reverts, which is
                // the safe outcome either way.
                try
                {
                    await client.ExecuteAsync(
                        RouterOsWriteCommands.SchedulerRemove, new[] { "=numbers=" + entryName }, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort only.
                }

                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception ex)
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            stopwatch.Stop();
            var error = MapError(ex);
            _metrics.RecordFailure("setAccessVlan", stopwatch.Elapsed);
            _logger.LogWarning(
                "RouterOS setAccessVlan failed for {Host} port {Port}: {Code} (retryable={Retryable})",
                _host, request.PortName, error.Code, error.Retryable);
            return new PendingChange(DriverResult<SetAccessVlanOutcome>.Fail(error, stopwatch.Elapsed), null, null);
        }
    }

    /// <summary>
    /// Test seam: sends the confirm signal (<c>/system/scheduler/remove</c>) for a
    /// <see cref="PendingChange"/> produced by <see cref="BeginChangeAsync"/> and finalizes the outcome.
    /// For a change that was already terminal (dry-run, no-op, rejected, verification-failed, or an
    /// infra failure) this is a harmless pass-through — there is nothing left to confirm.
    /// </summary>
    internal async Task<DriverResult<SetAccessVlanOutcome>> ConfirmChangeAsync(
        PendingChange pending, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pending);

        if (pending.Client is null)
        {
            return pending.Result;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(_budget);

            await pending.Client.ExecuteAsync(
                RouterOsWriteCommands.SchedulerRemove, new[] { "=numbers=" + pending.SchedulerEntryName }, budget.Token)
                .ConfigureAwait(false);

            var confirmedValue = pending.Result.Value! with { Confirmed = true };
            stopwatch.Stop();
            _metrics.RecordOutcome("setAccessVlan", "applied", stopwatch.Elapsed);
            _logger.LogInformation(
                "RouterOS setAccessVlan confirmed for {Host} port {Port} vlan {VlanId} correlation {CorrelationId}",
                _host, confirmedValue.PortName, confirmedValue.VlanId, confirmedValue.CorrelationId);

            return DriverResult<SetAccessVlanOutcome>.Ok(confirmedValue, pending.Result.Duration + stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var error = MapError(ex);
            _metrics.RecordFailure("setAccessVlan", stopwatch.Elapsed);
            _logger.LogWarning(
                "RouterOS setAccessVlan confirm failed for {Host} port {Port}: {Code}",
                _host, pending.Result.Value?.PortName, error.Code);
            return DriverResult<SetAccessVlanOutcome>.Fail(error, stopwatch.Elapsed);
        }
        finally
        {
            await pending.Client.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Test/diagnostic seam: re-reads a port's PVID (via a fresh connection) and reports whether an
    /// applied-but-unconfirmed change has since self-reverted — the "follow-up verification" AC4 allows
    /// for surfacing <see cref="SwitchChangeReasonCode.AutoRolledBack"/>, since the driver has no way to
    /// observe an asynchronous device-side revert during the original call.
    /// </summary>
    internal async Task<DriverResult<SetAccessVlanOutcome>> CheckForAutoRollbackAsync(
        SetAccessVlanOutcome appliedOutcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(appliedOutcome);

        var stopwatch = Stopwatch.StartNew();
        IRouterOsWriteApiClient? client = null;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(_budget);

            client = _clientFactory();
            await client.ConnectAsync(budget.Token).ConfigureAwait(false);

            var rows = await client.ExecuteAsync(
                RouterOsWriteCommands.BridgePortPrint, new[] { "?interface=" + appliedOutcome.PortName }, budget.Token)
                .ConfigureAwait(false);
            var observedPvid = rows.Count == 1 ? new RouterOsRecord(rows[0]).GetInt("pvid") : null;

            var rolledBack = observedPvid is not null
                && observedPvid == appliedOutcome.Before?.Pvid
                && observedPvid != appliedOutcome.VlanId;

            var after = (appliedOutcome.Before ?? new SwitchAccessVlanState(appliedOutcome.PortName, null, Array.Empty<int>()))
                with
            { Pvid = observedPvid };
            var reasonCode = rolledBack ? SwitchChangeReasonCode.AutoRolledBack : appliedOutcome.ReasonCode;
            var verification = new VerificationResult(!rolledBack, appliedOutcome.VlanId, observedPvid,
                rolledBack ? "The change was not confirmed within its window and the device reverted it." : null);

            stopwatch.Stop();
            _metrics.RecordOutcome("setAccessVlan", rolledBack ? "rolledback" : "applied", stopwatch.Elapsed);

            var audit = appliedOutcome.Audit with
            {
                After = after,
                ReasonCode = reasonCode,
                Verification = verification,
                OccurredAtUtc = _timeProvider.GetUtcNow(),
            };
            var outcome = appliedOutcome with
            {
                After = after,
                Verification = verification,
                Confirmed = !rolledBack && appliedOutcome.Confirmed,
                ReasonCode = reasonCode,
                Audit = audit,
            };

            return DriverResult<SetAccessVlanOutcome>.Ok(outcome, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var error = MapError(ex);
            _metrics.RecordFailure("setAccessVlan", stopwatch.Elapsed);
            return DriverResult<SetAccessVlanOutcome>.Fail(error, stopwatch.Elapsed);
        }
        finally
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private PendingChange Finish(
        SetAccessVlanRequest request, TimeSpan confirmWindow, SwitchChangePlan plan,
        SwitchAccessVlanState? before, SwitchAccessVlanState? after, VerificationResult? verification,
        bool confirmed, SwitchChangeReasonCode reasonCode, Stopwatch stopwatch)
    {
        stopwatch.Stop();
        var result = BuildResult(request, confirmWindow, plan, before, after, verification, confirmed, reasonCode, stopwatch.Elapsed);

        var outcomeTag = reasonCode switch
        {
            SwitchChangeReasonCode.Applied => "applied",
            SwitchChangeReasonCode.AutoRolledBack => "rolledback",
            SwitchChangeReasonCode.NoOpAlreadyDesiredState or SwitchChangeReasonCode.DryRunPlanned => "noop",
            _ => "failed",
        };
        _metrics.RecordOutcome("setAccessVlan", outcomeTag, stopwatch.Elapsed);
        _logger.LogInformation(
            "RouterOS setAccessVlan {Host} port {Port} vlan {VlanId} dryRun={DryRun} outcome {ReasonCode}",
            _host, request.PortName, request.DesiredVlanId, request.DryRun, reasonCode);

        return new PendingChange(result, null, null);
    }

    private DriverResult<SetAccessVlanOutcome> BuildResult(
        SetAccessVlanRequest request, TimeSpan confirmWindow, SwitchChangePlan plan,
        SwitchAccessVlanState? before, SwitchAccessVlanState? after, VerificationResult? verification,
        bool confirmed, SwitchChangeReasonCode reasonCode, TimeSpan elapsed)
    {
        var audit = new SwitchChangeAuditRecord(
            request.CorrelationId, _host, request.PortName, request.DesiredVlanId, request.DryRun,
            confirmWindow.TotalSeconds, before, after, reasonCode, verification, _timeProvider.GetUtcNow(),
            request.ActorType, request.RequestedBy);

        var outcome = new SetAccessVlanOutcome(
            _host, request.PortName, request.DesiredVlanId, request.CorrelationId, request.DryRun,
            plan, before, after, verification, confirmed, reasonCode, audit);

        return DriverResult<SetAccessVlanOutcome>.Ok(outcome, elapsed);
    }

    private static string BuildSchedulerEntryName(Guid correlationId)
        => "caisson-revert-" + correlationId.ToString("N").Substring(0, 12);

    private static IReadOnlyList<int> ExtractUntaggedVlanIds(
        IReadOnlyList<IReadOnlyDictionary<string, string>> bridgeVlanRows, string portName)
    {
        var ids = new SortedSet<int>();
        foreach (var row in bridgeVlanRows)
        {
            var record = new RouterOsRecord(row);
            var untagged = record.GetString("untagged");
            if (untagged is null)
            {
                continue;
            }

            var ports = untagged.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (Array.IndexOf(ports, portName) < 0)
            {
                continue;
            }

            foreach (var id in RouterOsMappers.ParseVlanIds(record.GetString("vlan-ids", "vlan-id")))
            {
                ids.Add(id);
            }
        }

        return ids.ToArray();
    }

    private static bool IsVlanConfigured(
        IReadOnlyList<IReadOnlyDictionary<string, string>> bridgeVlanRows, int vlanId)
    {
        foreach (var row in bridgeVlanRows)
        {
            var record = new RouterOsRecord(row);
            if (RouterOsMappers.ParseVlanIds(record.GetString("vlan-ids", "vlan-id")).Contains(vlanId))
            {
                return true;
            }
        }

        return false;
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
            DriverErrorCode.ConnectionTimeout, "RouterOS write operation exceeded its overall time budget.", Retryable: true),
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

    /// <summary>
    /// The result of <see cref="BeginChangeAsync"/>: either an already-terminal
    /// <see cref="DriverResult{T}"/> (<see cref="Client"/> is <c>null</c>), or an applied-and-verified
    /// change awaiting the confirm signal, carrying the still-open <see cref="Client"/> and the armed
    /// scheduler entry name for <see cref="ConfirmChangeAsync"/> to use.
    /// </summary>
    internal sealed record PendingChange(
        DriverResult<SetAccessVlanOutcome> Result, IRouterOsWriteApiClient? Client, string? SchedulerEntryName);
}
