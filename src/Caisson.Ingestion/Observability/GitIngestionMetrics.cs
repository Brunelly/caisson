using System.Diagnostics.Metrics;

namespace Caisson.Ingestion.Observability;

/// <summary>
/// Observability for Git-backed desired-state ingestion (story #62, NFR4/NFR6). A single <see cref="Meter"/>
/// owning the counters/histogram operators watch: run outcomes by category, run duration (NFR4's 30s P95
/// budget), and webhook signature/replay rejections. Mirrors <c>TopologyMetrics</c>'s shape. Registered
/// as a singleton.
/// </summary>
public sealed class GitIngestionMetrics : IDisposable
{
    /// <summary>The meter name; subscribe to this via <c>System.Diagnostics.Metrics</c>/OpenTelemetry.</summary>
    public const string MeterName = "Caisson.Ingestion";

    private readonly Meter _meter;
    private readonly Counter<long> _runsStarted;
    private readonly Counter<long> _runsSucceeded;
    private readonly Counter<long> _runsPartiallySucceeded;
    private readonly Counter<long> _runsValidationFailed;
    private readonly Counter<long> _runsInfraFailed;
    private readonly Histogram<double> _runDurationSeconds;
    private readonly Counter<long> _webhookSignatureRejections;
    private readonly Counter<long> _webhookReplayRejections;

    public GitIngestionMetrics()
    {
        _meter = new Meter(MeterName);
        _runsStarted = _meter.CreateCounter<long>(
            "caisson.ingestion.runs_started", unit: "{run}", description: "Desired-state ingestion runs started (poll or webhook).");
        _runsSucceeded = _meter.CreateCounter<long>(
            "caisson.ingestion.runs_succeeded", unit: "{run}", description: "Runs where every rack file validated.");
        _runsPartiallySucceeded = _meter.CreateCounter<long>(
            "caisson.ingestion.runs_partially_succeeded", unit: "{run}", description: "Runs where some but not all rack files validated (Q3).");
        _runsValidationFailed = _meter.CreateCounter<long>(
            "caisson.ingestion.runs_validation_failed", unit: "{run}", description: "Runs where no rack file validated.");
        _runsInfraFailed = _meter.CreateCounter<long>(
            "caisson.ingestion.runs_infra_failed", unit: "{run}", description: "Runs that could not complete for an infrastructure reason (auth/network/parse/persistence).");
        _runDurationSeconds = _meter.CreateHistogram<double>(
            "caisson.ingestion.run_duration_seconds", unit: "s", description: "Ingestion run wall-clock duration (NFR4: P95 < 30s).");
        _webhookSignatureRejections = _meter.CreateCounter<long>(
            "caisson.ingestion.webhook_signature_rejections", unit: "{request}", description: "Webhook deliveries rejected for an invalid/missing HMAC signature (NFR1).");
        _webhookReplayRejections = _meter.CreateCounter<long>(
            "caisson.ingestion.webhook_replay_rejections", unit: "{request}", description: "Webhook deliveries that replayed an already-processed delivery id (NFR2).");
    }

    /// <summary>Records that a run started.</summary>
    public void RecordRunStarted() => _runsStarted.Add(1);

    /// <summary>Records a run's terminal outcome and its wall-clock duration.</summary>
    public void RecordRunOutcome(IngestionRunOutcome outcome, TimeSpan duration)
    {
        switch (outcome)
        {
            case IngestionRunOutcome.Succeeded:
                _runsSucceeded.Add(1);
                break;
            case IngestionRunOutcome.PartiallySucceeded:
                _runsPartiallySucceeded.Add(1);
                break;
            case IngestionRunOutcome.ValidationFailed:
                _runsValidationFailed.Add(1);
                break;
            case IngestionRunOutcome.InfraFailed:
                _runsInfraFailed.Add(1);
                break;
        }

        _runDurationSeconds.Record(duration.TotalSeconds);
    }

    /// <summary>Records a webhook delivery rejected for an invalid/missing signature.</summary>
    public void RecordWebhookSignatureRejection() => _webhookSignatureRejections.Add(1);

    /// <summary>Records a webhook delivery rejected as an already-processed replay.</summary>
    public void RecordWebhookReplayRejection() => _webhookReplayRejections.Add(1);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}

/// <summary>The terminal outcome category of an ingestion run, for metrics purposes.</summary>
public enum IngestionRunOutcome
{
    Succeeded,
    PartiallySucceeded,
    ValidationFailed,
    InfraFailed,
}
