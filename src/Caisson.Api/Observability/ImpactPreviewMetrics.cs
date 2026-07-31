using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Caisson.Api.Observability;

/// <summary>The outcome of an impact-preview operation, used as a metric/audit tag.</summary>
public enum ImpactPreviewOutcome
{
    /// <summary>A diff was returned (computed or served from cache).</summary>
    Success,

    /// <summary>The candidate YAML was invalid (400).</summary>
    Invalid,

    /// <summary>The rack has no ingested baseline revision (409).</summary>
    MissingBaseline,
}

/// <summary>
/// Metrics for the story-#171 impact-preview endpoints (Task #202, NFR4) — a counter of operations tagged
/// <c>operation</c> (compute|cache-hit) and <c>outcome</c> (from which the cache-hit ratio derives) plus a
/// <c>diff_compute_seconds</c> histogram. Mirrors <see cref="Caisson.Ingestion.Observability.PreflightValidationMetrics"/>:
/// a DI singleton, low-cardinality tags only (never rack id, hash, actor, or payload), so the series stays
/// secret-free and bounded.
/// </summary>
public sealed class ImpactPreviewMetrics : IDisposable
{
    /// <summary>The meter name emitting the impact-preview instruments.</summary>
    public const string MeterName = "Caisson.Api.ImpactPreview";

    private readonly Meter _meter;
    private readonly Counter<long> _operations;
    private readonly Histogram<double> _diffComputeSeconds;

    public ImpactPreviewMetrics()
    {
        _meter = new Meter(MeterName);
        _operations = _meter.CreateCounter<long>(
            "caisson.impact_preview.operations",
            unit: "{operation}",
            description: "Impact-preview operations, tagged by operation (compute|cache-hit) and outcome; cache_hit_ratio derives from this.");
        _diffComputeSeconds = _meter.CreateHistogram<double>(
            "caisson.impact_preview.diff_compute_seconds",
            unit: "s",
            description: "Impact-preview diff compute wall-clock duration (compute path only; NFR1 P95 <= 800ms).");
    }

    /// <summary>Records a freshly-computed preview (cache miss) and its diff-compute duration.</summary>
    public void RecordCompute(ImpactPreviewOutcome outcome, TimeSpan diffComputeDuration)
    {
        Record("compute", outcome);
        _diffComputeSeconds.Record(diffComputeDuration.TotalSeconds, Tags("compute", outcome));
    }

    /// <summary>Records a cache-hit preview (no recomputation).</summary>
    public void RecordCacheHit()
        => Record("cache-hit", ImpactPreviewOutcome.Success);

    /// <summary>Records a rejected preview (invalid YAML / missing baseline) that computed no diff.</summary>
    public void RecordRejected(ImpactPreviewOutcome outcome)
        => Record("compute", outcome);

    private void Record(string operation, ImpactPreviewOutcome outcome)
        => _operations.Add(1, Tags(operation, outcome));

    private static TagList Tags(string operation, ImpactPreviewOutcome outcome)
        => new()
        {
            { "operation", operation },
            { "outcome", outcome.ToString().ToLowerInvariant() },
        };

    public void Dispose() => _meter.Dispose();
}
