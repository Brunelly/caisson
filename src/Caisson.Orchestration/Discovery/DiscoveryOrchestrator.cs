using System.Text.Json;
using Caisson.Correlation;
using Caisson.Correlation.Input;
using Caisson.Correlation.Results;
using Caisson.Domain.Discovery;
using Caisson.Domain.Enums;
using Caisson.Infrastructure.Persistence.Ingestion;
using Caisson.Orchestration.Options;
using Caisson.Orchestration.RackDefinitions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caisson.Orchestration.Discovery;

/// <summary>
/// The four-step discovery pipeline (story #8). Mirrors the story-7 <c>PersistQueryRunner</c> template
/// for the correlation + persistence steps. Read-only discovery and pure correlation are re-run on
/// resume (idempotent, no side effects); only the persistence step is guarded by
/// <see cref="DiscoveryJob.ResultSnapshotId"/> so a confirmed snapshot is never written twice.
/// </summary>
public sealed class DiscoveryOrchestrator : IDiscoveryOrchestrator
{
    private const string Source = "DiscoveryOrchestration";

    private readonly IDeviceDiscoveryService _devices;
    private readonly IRackDefinitionProvider _rackDefinitions;
    private readonly ITopologyCorrelationEngine _engine;
    private readonly ITopologySnapshotIngestionService _ingestion;
    private readonly IDiscoveryJobStore _store;
    private readonly TimeProvider _time;
    private readonly DiscoveryOrchestrationOptions _options;
    private readonly ILogger<DiscoveryOrchestrator> _logger;

    public DiscoveryOrchestrator(
        IDeviceDiscoveryService devices,
        IRackDefinitionProvider rackDefinitions,
        ITopologyCorrelationEngine engine,
        ITopologySnapshotIngestionService ingestion,
        IDiscoveryJobStore store,
        TimeProvider time,
        IOptions<DiscoveryOrchestrationOptions> options,
        ILogger<DiscoveryOrchestrator> logger)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _rackDefinitions = rackDefinitions ?? throw new ArgumentNullException(nameof(rackDefinitions));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _ingestion = ingestion ?? throw new ArgumentNullException(nameof(ingestion));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task RunAsync(DiscoveryJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var context = new DeviceDiscoveryContext(job.CorrelationId, job.RackId, job.Id);
        var startedAtUtc = job.StartedAtUtc ?? Now;

        RackDefinition definition;
        try
        {
            definition = await _rackDefinitions.GetAsync(job.RackId, cancellationToken);
        }
        catch (RackDefinitionMissingException)
        {
            await FailJobAsync(job, DiscoveryErrorCodes.RackDefinitionMissing,
                "No discovery definition is configured for the rack.", cancellationToken);
            return;
        }

        try
        {
            if (await IsCanceledAsync(job, cancellationToken))
            {
                return;
            }

            var switches = await ExecuteStepAsync(
                job, DiscoveryStepName.SwitchDiscovery, cancellationToken,
                () => _devices.DiscoverSwitchesAsync(definition, context, cancellationToken),
                o => Summarize(new { attempted = o.Attempted, failed = o.Failed, discovered = o.Switches.Count }));

            if (await IsCanceledAsync(job, cancellationToken))
            {
                return;
            }

            var servers = await ExecuteStepAsync(
                job, DiscoveryStepName.BmcDiscovery, cancellationToken,
                () => _devices.DiscoverServersAsync(definition, context, cancellationToken),
                o => Summarize(new { attempted = o.Attempted, failed = o.Failed, discovered = o.Servers.Count }));

            if (await IsCanceledAsync(job, cancellationToken))
            {
                return;
            }

            var input = new TopologyCorrelationInput(switches.Switches, servers.Servers);
            var correlation = await ExecuteStepAsync(
                job, DiscoveryStepName.Correlation, cancellationToken,
                () => Task.FromResult(_engine.Correlate(input)),
                r => Summarize(new
                {
                    mapped = r.Mappings.Count,
                    ambiguous = r.AmbiguousMappings.Count,
                    unmappedNics = r.UnmappedNics.Count,
                    unmappedPorts = r.UnmappedPorts.Count,
                }));

            if (await IsCanceledAsync(job, cancellationToken))
            {
                return;
            }

            var status = switches.IsPartial || servers.IsPartial
                ? SnapshotStatus.PartialSuccess
                : SnapshotStatus.Completed;

            await PersistAsync(job, input, correlation, status, startedAtUtc, cancellationToken);

            job.Succeed(Now);
            await _store.SaveAsync(cancellationToken);
            _logger.LogInformation(
                "Discovery job succeeded jobId={JobId} rackId={RackId} correlationId={CorrelationId} " +
                "snapshotId={SnapshotId} status={Status}",
                job.Id, job.RackId, job.CorrelationId, job.ResultSnapshotId, status);
        }
        catch (JobAbortedException aborted)
        {
            _logger.LogWarning(
                "Discovery job failed jobId={JobId} rackId={RackId} correlationId={CorrelationId} errorCode={ErrorCode}",
                job.Id, job.RackId, job.CorrelationId, aborted.ErrorCode);
        }
        catch (OperationCanceledException)
        {
            // Distinguish an operator/admin cancel (durable flag set) from a host-shutdown pause.
            if (await IsCancellationRequestedAsync(job, CancellationToken.None))
            {
                await CancelJobAsync(job, CancellationToken.None);
                return;
            }

            _logger.LogInformation(
                "Discovery job paused for restart jobId={JobId} rackId={RackId} correlationId={CorrelationId}",
                job.Id, job.RackId, job.CorrelationId);
            throw;
        }
    }

    private async Task<T> ExecuteStepAsync<T>(
        DiscoveryJob job,
        DiscoveryStepName stepName,
        CancellationToken cancellationToken,
        Func<Task<T>> action,
        Func<T, string?> summarize)
    {
        var step = FindStep(job, stepName);

        for (var attempt = 1; ; attempt++)
        {
            step.BeginAttempt(Now);
            job.Heartbeat(Now);
            await _store.SaveAsync(cancellationToken);

            try
            {
                var value = await action();
                step.Succeed(Now, summarize(value));
                job.Heartbeat(Now);
                await _store.SaveAsync(cancellationToken);
                return value;
            }
            catch (DiscoveryStepException ex)
            {
                if (!ex.Retryable || attempt >= _options.MaxStepAttempts)
                {
                    await FailStepAndJobAsync(job, step, ex.ErrorCode, ex.Message, cancellationToken);
                }

                _logger.LogWarning(
                    "Discovery step retrying stepName={StepName} attempt={Attempt} jobId={JobId} errorCode={ErrorCode}",
                    stepName, attempt, job.Id, ex.ErrorCode);
                await BackoffAsync(attempt, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not JobAbortedException)
            {
                if (attempt >= _options.MaxStepAttempts)
                {
                    await FailStepAndJobAsync(
                        job, step, DiscoveryErrorCodes.UnexpectedError, ex.Message, cancellationToken);
                }

                _logger.LogWarning(
                    ex, "Discovery step threw, retrying stepName={StepName} attempt={Attempt} jobId={JobId}",
                    stepName, attempt, job.Id);
                await BackoffAsync(attempt, cancellationToken);
            }
        }
    }

    private async Task PersistAsync(
        DiscoveryJob job,
        TopologyCorrelationInput input,
        TopologyCorrelationResult correlation,
        SnapshotStatus status,
        DateTime startedAtUtc,
        CancellationToken cancellationToken)
    {
        var step = FindStep(job, DiscoveryStepName.Persistence);

        // Idempotency guard: a confirmed persist is never repeated (AC1).
        if (job.ResultSnapshotId is not null)
        {
            if (step.Status != DiscoveryStepStatus.Succeeded)
            {
                step.Skip(Now);
                await _store.SaveAsync(cancellationToken);
            }

            _logger.LogInformation(
                "Persistence already complete, skipping jobId={JobId} snapshotId={SnapshotId}",
                job.Id, job.ResultSnapshotId);
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            step.BeginAttempt(Now);
            job.Heartbeat(Now);
            await _store.SaveAsync(cancellationToken);

            try
            {
                var request = new TopologyIngestionRequest(
                    job.RackId, input, correlation, job.Mode, job.TriggeredBy, job.ActorType,
                    Source, null, job.CorrelationId, status, startedAtUtc, Now);
                var outcome = await _ingestion.IngestAsync(request, cancellationToken);

                job.SetResultSnapshot(outcome.SnapshotId);
                step.Succeed(Now, Summarize(new { snapshotId = outcome.SnapshotId, version = outcome.Version }));
                job.Heartbeat(Now);
                await _store.SaveAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not JobAbortedException)
            {
                if (attempt >= _options.MaxStepAttempts)
                {
                    await FailStepAndJobAsync(
                        job, step, DiscoveryErrorCodes.PersistenceFailed, ex.Message, cancellationToken);
                }

                _logger.LogWarning(
                    ex, "Persistence step failed, retrying attempt={Attempt} jobId={JobId}", attempt, job.Id);
                await BackoffAsync(attempt, cancellationToken);
            }
        }
    }

    private async Task FailStepAndJobAsync(
        DiscoveryJob job, DiscoveryJobStep step, string errorCode, string message, CancellationToken cancellationToken)
    {
        step.Fail(Now, errorCode, message);
        SkipRemaining(job);
        job.Fail(Now, errorCode, message);
        await _store.SaveAsync(cancellationToken);
        throw new JobAbortedException(errorCode);
    }

    private async Task FailJobAsync(DiscoveryJob job, string errorCode, string message, CancellationToken cancellationToken)
    {
        SkipRemaining(job);
        job.Fail(Now, errorCode, message);
        await _store.SaveAsync(cancellationToken);
        _logger.LogWarning(
            "Discovery job failed before execution jobId={JobId} rackId={RackId} correlationId={CorrelationId} errorCode={ErrorCode}",
            job.Id, job.RackId, job.CorrelationId, errorCode);
    }

    private async Task<bool> IsCanceledAsync(DiscoveryJob job, CancellationToken cancellationToken)
    {
        if (!await IsCancellationRequestedAsync(job, cancellationToken))
        {
            return false;
        }

        await CancelJobAsync(job, cancellationToken);
        return true;
    }

    private async Task<bool> IsCancellationRequestedAsync(DiscoveryJob job, CancellationToken cancellationToken)
        => job.CancellationRequested || await _store.IsCancellationRequestedAsync(job.Id, cancellationToken);

    private async Task CancelJobAsync(DiscoveryJob job, CancellationToken cancellationToken)
    {
        job.RequestCancellation();
        foreach (var step in job.Steps)
        {
            if (step.Status is DiscoveryStepStatus.Pending or DiscoveryStepStatus.InProgress)
            {
                step.Skip(Now);
            }
        }

        job.Cancel(Now);
        await _store.SaveAsync(cancellationToken);
        _logger.LogInformation(
            "Discovery job canceled jobId={JobId} rackId={RackId} correlationId={CorrelationId}",
            job.Id, job.RackId, job.CorrelationId);
    }

    private void SkipRemaining(DiscoveryJob job)
    {
        foreach (var step in job.Steps)
        {
            if (step.Status is DiscoveryStepStatus.Pending)
            {
                step.Skip(Now);
            }
        }
    }

    private async Task BackoffAsync(int attempt, CancellationToken cancellationToken)
    {
        var delayMs = Math.Min(
            _options.RetryMaxDelayMs,
            _options.RetryBaseDelayMs * (int)Math.Pow(2, attempt - 1));
        if (delayMs > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), _time, cancellationToken);
        }
    }

    private static DiscoveryJobStep FindStep(DiscoveryJob job, DiscoveryStepName name)
        => job.Steps.FirstOrDefault(s => s.StepName == name)
           ?? throw new InvalidOperationException($"Job '{job.Id}' is missing step '{name}'.");

    private static string? Summarize(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return json.Length > DiscoveryJobStep.MaxResultSummaryJsonLength
            ? json[..DiscoveryJobStep.MaxResultSummaryJsonLength]
            : json;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>Internal control-flow signal that a step failed the job (state already persisted).</summary>
    private sealed class JobAbortedException : Exception
    {
        public JobAbortedException(string errorCode) => ErrorCode = errorCode;

        public string ErrorCode { get; }
    }
}
