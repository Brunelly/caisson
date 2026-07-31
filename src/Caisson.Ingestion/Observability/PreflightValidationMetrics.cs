using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Caisson.Ingestion.Observability;

/// <summary>The outcome of a pre-flight validation or PR-gate operation, used as a metric/audit tag.</summary>
public enum PreflightValidationOutcome
{
    /// <summary>Validation passed with no errors (warnings may still be present).</summary>
    Valid,

    /// <summary>Validation found one or more blocking errors.</summary>
    Invalid,

    /// <summary>The PR gate passed (correct run id + all warnings acknowledged).</summary>
    Created,

    /// <summary>The PR gate rejected the request (errors, run-id mismatch, or an unacknowledged warning).</summary>
    Rejected,
}

/// <summary>
/// Metrics for the story-#170 pre-flight validation + PR-gate endpoints — a counter of operations plus a
/// wall-clock duration histogram for the NFR2 P95 ≤ 500ms target. Mirrors
/// <see cref="DesiredStateRoundTripMetrics"/> exactly: a DI singleton, outcome/operation-tagged only
/// (never rack id, payload, or any identifier), so the series stays low-cardinality and secret-free.
/// </summary>
public sealed class PreflightValidationMetrics : IDisposable
{
    /// <summary>The meter name emitting the pre-flight validation instruments.</summary>
    public const string MeterName = "Caisson.Ingestion.Preflight";

    private readonly Meter _meter;
    private readonly Counter<long> _operations;
    private readonly Histogram<double> _durationSeconds;

    public PreflightValidationMetrics()
    {
        _meter = new Meter(MeterName);
        _operations = _meter.CreateCounter<long>(
            "caisson.network_config.preflight_operations",
            unit: "{operation}",
            description: "Pre-flight validation / PR-gate operations, tagged by operation (validate|create-pr) and outcome.");
        _durationSeconds = _meter.CreateHistogram<double>(
            "caisson.network_config.preflight_duration_seconds",
            unit: "s",
            description: "Pre-flight validation / PR-gate wall-clock duration (NFR2: P95 <= 500ms).");
    }

    /// <summary>Records a preflight-validate operation and its duration.</summary>
    public void RecordValidate(PreflightValidationOutcome outcome, TimeSpan duration)
        => Record("validate", outcome, duration);

    /// <summary>Records a PR-gate (create-pr) operation and its duration.</summary>
    public void RecordCreatePr(PreflightValidationOutcome outcome, TimeSpan duration)
        => Record("create-pr", outcome, duration);

    private void Record(string operation, PreflightValidationOutcome outcome, TimeSpan duration)
    {
        var tags = new TagList
        {
            { "operation", operation },
            { "outcome", outcome.ToString().ToLowerInvariant() },
        };
        _operations.Add(1, tags);
        _durationSeconds.Record(duration.TotalSeconds, tags);
    }

    public void Dispose() => _meter.Dispose();
}
