using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Caisson.Orchestration.DriftApply;

/// <summary>
/// Job-outcome and apply-latency metrics for the drift-apply pipeline (story #65), built on
/// <see cref="Meter"/> — mirrors <c>RouterOsWriteMetrics</c>'s shape. Emits a duration histogram (from
/// request to terminal state) and an outcome counter, each tagged <c>status</c> and, on a terminal
/// outcome, <c>reasonCode</c> (a <c>SwitchChangeReasonCode</c> name or a <c>DriftApplyErrorCodes</c>
/// value — never a raw exception).
/// </summary>
public sealed class DriftApplyMetrics : IDisposable
{
    /// <summary>The meter name to enable when configuring an OpenTelemetry/metrics listener.</summary>
    public const string MeterName = "Caisson.Orchestration.DriftApply";

    private readonly Meter _meter;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _outcomes;

    /// <summary>Creates the meter and its instruments.</summary>
    public DriftApplyMetrics()
    {
        _meter = new Meter(MeterName);
        _duration = _meter.CreateHistogram<double>(
            "caisson.drift_apply.job.duration", unit: "ms",
            description: "Drift-apply job duration from request to terminal state.");
        _outcomes = _meter.CreateCounter<long>(
            "caisson.drift_apply.job.count", unit: "{job}",
            description: "Drift-apply job terminal outcomes by status and reason code.");
    }

    /// <summary>Records a job reaching a terminal state.</summary>
    public void RecordTerminal(string status, string? reasonCode, TimeSpan duration)
    {
        var tags = new TagList
        {
            { "status", status },
            { "reasonCode", reasonCode ?? "none" },
        };

        _duration.Record(duration.TotalMilliseconds, tags);
        _outcomes.Add(1, tags);
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
