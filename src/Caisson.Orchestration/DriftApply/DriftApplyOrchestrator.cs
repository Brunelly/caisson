using System.Globalization;
using System.Text.Json;
using Caisson.Domain.Drift.Apply;
using Caisson.Domain.Enums;
using Caisson.Drivers.Abstractions.Identity;
using Caisson.Drivers.Abstractions.Mutating;
using Caisson.Drivers.Abstractions.Registry;
using Caisson.Infrastructure.LiveUpdates;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Drift;
using Caisson.Infrastructure.Persistence.Queries;
using Caisson.Orchestration.Options;
using Caisson.Orchestration.RackDefinitions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caisson.Orchestration.DriftApply;

/// <summary>
/// The two-step drift-apply pipeline (story #65): Revalidation re-diffs desired-vs-observed for the
/// job's rack and confirms the target drift item is still present and unchanged (AC3, Q3's "Both"
/// answer) before DeviceApply drives the single #66 <c>ISwitchMutatingDriver.SetAccessVlanAsync</c> call.
/// Both steps are idempotent on resume: revalidation is skipped once its resolved target is persisted, and
/// DeviceApply is skipped once <see cref="DriftApplyJob.RecordDeviceOutcome"/> has recorded an outcome —
/// the crash-resume guard that limits a job to at most one device write (AC4/NFR2).
/// </summary>
public sealed class DriftApplyOrchestrator : IDriftApplyOrchestrator
{
    private readonly CaissonDbContext _context;
    private readonly IDriftComputationService _driftComputation;
    private readonly IRackDefinitionProvider _rackDefinitions;
    private readonly ISwitchMutatingDriverRegistry _driverRegistry;
    private readonly IDriftRecomputeSignal _driftRecompute;
    private readonly ITopologyEventPublisher _events;
    private readonly ITopologyEventSequencer _sequencer;
    private readonly TimeProvider _time;
    private readonly DriftApplyOrchestrationOptions _options;
    private readonly ILogger<DriftApplyOrchestrator> _logger;

    public DriftApplyOrchestrator(
        CaissonDbContext context,
        IDriftComputationService driftComputation,
        IRackDefinitionProvider rackDefinitions,
        ISwitchMutatingDriverRegistry driverRegistry,
        IDriftRecomputeSignal driftRecompute,
        ITopologyEventPublisher events,
        ITopologyEventSequencer sequencer,
        TimeProvider time,
        IOptions<DriftApplyOrchestrationOptions> options,
        ILogger<DriftApplyOrchestrator> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _driftComputation = driftComputation ?? throw new ArgumentNullException(nameof(driftComputation));
        _rackDefinitions = rackDefinitions ?? throw new ArgumentNullException(nameof(rackDefinitions));
        _driverRegistry = driverRegistry ?? throw new ArgumentNullException(nameof(driverRegistry));
        _driftRecompute = driftRecompute ?? throw new ArgumentNullException(nameof(driftRecompute));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _sequencer = sequencer ?? throw new ArgumentNullException(nameof(sequencer));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task RunAsync(DriftApplyJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        try
        {
            if (job.SwitchDeviceKey is null)
            {
                var previousStatus = job.Status.ToString();
                job.MarkRevalidating(Now);
                await _context.SaveChangesAsync(cancellationToken);
                await PublishStatusAsync(job, previousStatus, DriftApplyStepName.Revalidation.ToString(), reasonCode: null, cancellationToken);

                var revalidation = await ExecuteStepAsync(
                    job, DriftApplyStepName.Revalidation, cancellationToken,
                    ct => RevalidateAsync(job, ct),
                    o => Summarize(new { current = o.IsCurrent, reasonCode = o.ReasonCode }));

                if (!revalidation.IsCurrent)
                {
                    await MarkStaleAndFinalizeAsync(job, revalidation, cancellationToken);
                    return;
                }

                job.ResolveTarget(revalidation.SwitchDeviceKey!, revalidation.PortName!, revalidation.DesiredVlanId);
                await _context.SaveChangesAsync(cancellationToken);
            }
            else
            {
                _logger.LogInformation(
                    "Drift-apply revalidation already resolved on a prior attempt, resuming jobId={JobId} rackId={RackId} correlationId={CorrelationId}",
                    job.Id, job.RackId, job.CorrelationId);
            }

            var beforeExecuting = job.Status.ToString();
            job.MarkExecuting(Now);
            await _context.SaveChangesAsync(cancellationToken);
            await PublishStatusAsync(job, beforeExecuting, DriftApplyStepName.DeviceApply.ToString(), reasonCode: null, cancellationToken);

            await ExecuteStepAsync(
                job, DriftApplyStepName.DeviceApply, cancellationToken,
                ct => DeviceApplyAsync(job, ct),
                reasonCode => Summarize(new { reasonCode }));

            await FinalizeFromDeviceOutcomeAsync(job, cancellationToken);
        }
        catch (JobAbortedException)
        {
            // The step/job was already durably failed; a terminal event was already published by
            // FailStepAndJobAsync's caller (ExecuteStepAsync's callers publish via the runner's
            // finalize path once RunAsync returns).
        }
    }

    private async Task<RevalidationOutcome> RevalidateAsync(DriftApplyJob job, CancellationToken cancellationToken)
    {
        // Never throws (IDriftComputationService's own contract) — a recompute failure is recorded as a
        // Failed DriftReport, which then simply means the item lookup below finds nothing (stale).
        await _driftComputation.ComputeAndPersistAsync(job.RackId, job.CorrelationId, cancellationToken);

        var item = await _context.ItemByDriftItemIdAsync(job.RackId, job.DriftItemId, cancellationToken);
        if (item is null)
        {
            return RevalidationOutcome.Stale(DriftApplyErrorCodes.DriftItemGone, comparedDriftReportId: null, comparedDriftItemId: null);
        }

        var currentAfter = ParseVlan(item.ExpectedValue);
        var currentBefore = ParseNullableVlan(item.ActualValue);
        if (currentAfter != job.ExpectedAfterVlan || currentBefore != job.ExpectedBeforeVlan)
        {
            return RevalidationOutcome.Stale(DriftApplyErrorCodes.DriftAnchorsMismatched, item.DriftReportId, item.DriftItemId);
        }

        var (switchName, portName) = ParseSwitchAndPort(item.DetailsJson);
        if (switchName is null || portName is null)
        {
            _logger.LogWarning(
                "Drift item is missing switchName/portName details, treating as stale jobId={JobId} driftItemId={DriftItemId} correlationId={CorrelationId}",
                job.Id, job.DriftItemId, job.CorrelationId);
            return RevalidationOutcome.Stale(DriftApplyErrorCodes.DriftAnchorsMismatched, item.DriftReportId, item.DriftItemId);
        }

        return RevalidationOutcome.Current(switchName, portName, currentAfter);
    }

    private async Task MarkStaleAndFinalizeAsync(DriftApplyJob job, RevalidationOutcome outcome, CancellationToken cancellationToken)
    {
        var step = FindStep(job, DriftApplyStepName.DeviceApply);
        step.Skip(Now);

        var details = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["expectedDriftReportId"] = job.ExpectedDriftReportId,
            ["comparedDriftReportId"] = outcome.ComparedDriftReportId,
            ["comparedDriftItemId"] = outcome.ComparedDriftItemId,
        });

        job.MarkStaleDrift(Now, outcome.ReasonCode!, DriftApplyErrorCodes.MessageFor(outcome.ReasonCode!), details);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Drift-apply job found stale drift, no device call made jobId={JobId} rackId={RackId} reasonCode={ReasonCode} correlationId={CorrelationId}",
            job.Id, job.RackId, outcome.ReasonCode, job.CorrelationId);
    }

    private async Task<string> DeviceApplyAsync(DriftApplyJob job, CancellationToken cancellationToken)
    {
        if (job.DeviceReasonCode is not null)
        {
            // Crash-resume guard (AC4/NFR2): an outcome was already recorded on a prior attempt — the
            // device was already written to (or the write was already attempted and failed for reasons
            // that must not be retried). Never call the driver again for this job.
            _logger.LogInformation(
                "Drift-apply device outcome already recorded, skipping driver call jobId={JobId} reasonCode={ReasonCode} correlationId={CorrelationId}",
                job.Id, job.DeviceReasonCode, job.CorrelationId);
            return job.DeviceReasonCode;
        }

        RackDefinition definition;
        try
        {
            definition = await _rackDefinitions.GetAsync(job.RackId, cancellationToken);
        }
        catch (RackDefinitionMissingException)
        {
            throw new DriftApplyStepException(
                DriftApplyErrorCodes.RackDefinitionMissing,
                DriftApplyErrorCodes.MessageFor(DriftApplyErrorCodes.RackDefinitionMissing),
                retryable: false);
        }

        var device = definition.Switches.FirstOrDefault(s => string.Equals(s.DeviceKey, job.SwitchDeviceKey, StringComparison.Ordinal));
        if (device is null)
        {
            throw new DriftApplyStepException(
                DriftApplyErrorCodes.SwitchNotConfigured,
                DriftApplyErrorCodes.MessageFor(DriftApplyErrorCodes.SwitchNotConfigured),
                retryable: false);
        }

        if (!_driverRegistry.TryResolve(device.ToDescriptor(), out var factory))
        {
            throw new DriftApplyStepException(
                DriftApplyErrorCodes.DriverNotFound,
                DriftApplyErrorCodes.MessageFor(DriftApplyErrorCodes.DriverNotFound),
                retryable: false);
        }

        var driver = factory.Create(new SwitchMutatingConnectionOptions(
            device.Host, device.Port, device.Timeout, device.CredentialsRef, device.UseTls, device.AllowPlaintext));

        var request = new SetAccessVlanRequest(
            job.PortName!, job.DesiredVlanId!.Value, DryRun: false, ConfirmWindow: null,
            job.CorrelationId, job.RequestedBy, job.ActorType);

        var start = _time.GetTimestamp();
        var result = await driver.SetAccessVlanAsync(request, cancellationToken);
        var durationMs = (long)_time.GetElapsedTime(start).TotalMilliseconds;

        if (!result.Success)
        {
            _logger.LogWarning(
                "Drift-apply device call failed (infrastructure) jobId={JobId} rackId={RackId} switchDeviceKey={SwitchDeviceKey} " +
                "portName={PortName} durationMs={DurationMs} errorCode={ErrorCode} correlationId={CorrelationId}",
                job.Id, job.RackId, job.SwitchDeviceKey, job.PortName, durationMs, result.Error?.Code, job.CorrelationId);
            throw new DriftApplyStepException(
                DriftApplyErrorCodes.DeviceCallFailed,
                DriftApplyErrorCodes.MessageFor(DriftApplyErrorCodes.DeviceCallFailed),
                retryable: result.Error?.Retryable ?? true);
        }

        var outcome = result.Value!;
        job.RecordDeviceOutcome(
            outcome.ReasonCode.ToString(),
            outcome.Confirmed,
            outcome.Before is null ? null : JsonSerializer.Serialize(outcome.Before),
            outcome.After is null ? null : JsonSerializer.Serialize(outcome.After));
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Drift-apply device call completed jobId={JobId} rackId={RackId} switchDeviceKey={SwitchDeviceKey} portName={PortName} " +
            "reasonCode={ReasonCode} confirmed={Confirmed} durationMs={DurationMs} correlationId={CorrelationId}",
            job.Id, job.RackId, job.SwitchDeviceKey, job.PortName, outcome.ReasonCode, outcome.Confirmed, durationMs, job.CorrelationId);

        return outcome.ReasonCode.ToString();
    }

    private async Task FinalizeFromDeviceOutcomeAsync(DriftApplyJob job, CancellationToken cancellationToken)
    {
        var reasonCode = job.DeviceReasonCode is { } code && Enum.TryParse<SwitchChangeReasonCode>(code, out var parsed)
            ? parsed
            : SwitchChangeReasonCode.Unknown;

        if (reasonCode is SwitchChangeReasonCode.Applied or SwitchChangeReasonCode.NoOpAlreadyDesiredState)
        {
            job.Complete(Now);
            await _context.SaveChangesAsync(cancellationToken);

            // AC: a successful apply must be reflected in the next drift report (closing the loop).
            _driftRecompute.Enqueue(job.RackId);
            return;
        }

        job.Fail(
            Now, DriftApplyErrorCategories.DeviceRejected, job.DeviceReasonCode ?? SwitchChangeReasonCode.Unknown.ToString(),
            "The device change was not applied or could not be confirmed; no further attempt will be made.");
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<T> ExecuteStepAsync<T>(
        DriftApplyJob job,
        DriftApplyStepName stepName,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<T>> action,
        Func<T, string?> summarize)
    {
        var step = FindStep(job, stepName);

        for (var attempt = 1; ; attempt++)
        {
            step.BeginAttempt(Now);
            job.Heartbeat(Now);
            await _context.SaveChangesAsync(cancellationToken);

            try
            {
                var value = await action(cancellationToken);
                step.Succeed(Now, summarize(value));
                job.Heartbeat(Now);
                await _context.SaveChangesAsync(cancellationToken);
                return value;
            }
            catch (DriftApplyStepException ex)
            {
                if (!ex.Retryable || attempt >= _options.MaxStepAttempts)
                {
                    throw await FailStepAndJobAsync(
                        job, step, DriftApplyErrorCategories.Infrastructure, ex.ErrorCode, ex.Message, cancellationToken);
                }

                _logger.LogWarning(
                    "Drift-apply step retrying stepName={StepName} attempt={Attempt} jobId={JobId} errorCode={ErrorCode}",
                    stepName, attempt, job.Id, ex.ErrorCode);
                await BackoffAsync(attempt, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not JobAbortedException)
            {
                if (attempt >= _options.MaxStepAttempts)
                {
                    throw await FailStepAndJobAsync(
                        job, step, DriftApplyErrorCategories.Infrastructure, DriftApplyErrorCodes.UnexpectedError,
                        DriftApplyErrorCodes.MessageFor(DriftApplyErrorCodes.UnexpectedError), cancellationToken);
                }

                _logger.LogWarning(
                    ex, "Drift-apply step threw, retrying stepName={StepName} attempt={Attempt} jobId={JobId}",
                    stepName, attempt, job.Id);
                await BackoffAsync(attempt, cancellationToken);
            }
        }
    }

    private async Task<JobAbortedException> FailStepAndJobAsync(
        DriftApplyJob job, DriftApplyJobStep step, string errorCategory, string errorCode, string message, CancellationToken cancellationToken)
    {
        step.Fail(Now, errorCode, message);
        job.Fail(Now, errorCategory, errorCode, message);
        await _context.SaveChangesAsync(cancellationToken);
        return new JobAbortedException(errorCode);
    }

    private async Task BackoffAsync(int attempt, CancellationToken cancellationToken)
    {
        var delayMs = Math.Min(_options.RetryMaxDelayMs, _options.RetryBaseDelayMs * (int)Math.Pow(2, attempt - 1));
        if (delayMs > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), _time, cancellationToken);
        }
    }

    private Task PublishStatusAsync(
        DriftApplyJob job, string? previousStatus, string? currentStep, string? reasonCode, CancellationToken cancellationToken)
        => _events.PublishDriftApplyJobStatusAsync(
            _sequencer, _time, _logger,
            job.RackId, job.Id, job.Status.ToString(), previousStatus, currentStep, reasonCode,
            errorCode: job.ErrorCode, job.CorrelationId, cancellationToken);

    private static DriftApplyJobStep FindStep(DriftApplyJob job, DriftApplyStepName name)
        => job.Steps.FirstOrDefault(s => s.StepName == name)
           ?? throw new InvalidOperationException($"Drift-apply job '{job.Id}' is missing step '{name}'.");

    private static (string? SwitchName, string? PortName) ParseSwitchAndPort(string? detailsJson)
    {
        if (string.IsNullOrEmpty(detailsJson))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            var root = document.RootElement;
            var switchName = root.TryGetProperty("switchName", out var sw) ? sw.GetString() : null;
            var portName = root.TryGetProperty("portName", out var pn) ? pn.GetString() : null;
            return (switchName, portName);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static int ParseVlan(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vlan) ? vlan : -1;

    private static int? ParseNullableVlan(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vlan) ? vlan : null;

    private static string? Summarize(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return json.Length > DriftApplyJobStep.MaxResultSummaryJsonLength
            ? json[..DriftApplyJobStep.MaxResultSummaryJsonLength]
            : json;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>Internal control-flow signal that a step failed the job (state already persisted).</summary>
    private sealed class JobAbortedException : Exception
    {
        public JobAbortedException(string errorCode) => ErrorCode = errorCode;

        public string ErrorCode { get; }
    }

    private sealed record RevalidationOutcome(
        bool IsCurrent,
        string? SwitchDeviceKey,
        string? PortName,
        int DesiredVlanId,
        string? ReasonCode,
        Guid? ComparedDriftReportId,
        Guid? ComparedDriftItemId)
    {
        public static RevalidationOutcome Current(string switchDeviceKey, string portName, int desiredVlanId)
            => new(true, switchDeviceKey, portName, desiredVlanId, null, null, null);

        public static RevalidationOutcome Stale(string reasonCode, Guid? comparedDriftReportId, Guid? comparedDriftItemId)
            => new(false, null, null, 0, reasonCode, comparedDriftReportId, comparedDriftItemId);
    }
}
