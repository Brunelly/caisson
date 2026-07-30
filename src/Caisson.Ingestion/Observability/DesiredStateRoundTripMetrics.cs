using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Caisson.Ingestion.Observability;

/// <summary>The terminal outcome of a round-trip parse/render operation, for metrics/audit purposes.</summary>
public enum DesiredStateRoundTripOutcome
{
    /// <summary>The operation produced a valid model/document.</summary>
    Success,

    /// <summary>The input was rejected for a schema/semantic/syntax reason (a 400 to the caller).</summary>
    Invalid,

    /// <summary>The operation failed for an unexpected reason.</summary>
    Error,
}

/// <summary>
/// Observability for the desired-state YAML round-trip endpoints (story #169, Task #187 / NFR2/NFR4).
/// Mirrors <see cref="GitIngestionMetrics"/>: a single <see cref="Meter"/> owning parse/render counters
/// (tagged by <c>operation</c>+<c>outcome</c>) and duration histograms operators watch against the NFR2
/// P95 &lt; 500ms budget. Registered as a singleton.
/// </summary>
public sealed class DesiredStateRoundTripMetrics : IDisposable
{
    /// <summary>The meter name; subscribe to this via <c>System.Diagnostics.Metrics</c>/OpenTelemetry.</summary>
    public const string MeterName = "Caisson.Ingestion.RoundTrip";

    private readonly Meter _meter;
    private readonly Counter<long> _operations;
    private readonly Histogram<double> _durationSeconds;

    public DesiredStateRoundTripMetrics()
    {
        _meter = new Meter(MeterName);
        _operations = _meter.CreateCounter<long>(
            "caisson.desired_state.roundtrip_operations",
            unit: "{operation}",
            description: "Desired-state YAML round-trip operations, tagged by operation (parse|render) and outcome.");
        _durationSeconds = _meter.CreateHistogram<double>(
            "caisson.desired_state.roundtrip_duration_seconds",
            unit: "s",
            description: "Round-trip parse/render wall-clock duration (NFR2: P95 < 500ms).");
    }

    /// <summary>Records a parse operation's outcome and wall-clock duration.</summary>
    public void RecordParse(DesiredStateRoundTripOutcome outcome, TimeSpan duration)
        => Record("parse", outcome, duration);

    /// <summary>Records a render operation's outcome and wall-clock duration.</summary>
    public void RecordRender(DesiredStateRoundTripOutcome outcome, TimeSpan duration)
        => Record("render", outcome, duration);

    private void Record(string operation, DesiredStateRoundTripOutcome outcome, TimeSpan duration)
    {
        var tags = new TagList
        {
            { "operation", operation },
            { "outcome", outcome.ToString().ToLowerInvariant() },
        };
        _operations.Add(1, tags);
        _durationSeconds.Record(duration.TotalSeconds, tags);
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
