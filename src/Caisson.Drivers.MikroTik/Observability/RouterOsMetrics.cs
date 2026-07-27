using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Caisson.Drivers.MikroTik.Observability;

/// <summary>
/// Discovery metrics for the RouterOS driver (NFR6), built on <see cref="Meter"/> from
/// <c>System.Diagnostics.Metrics</c> — a BCL, AOT-safe API that any OpenTelemetry exporter can scrape
/// later without this assembly taking an OpenTelemetry SDK dependency. Emits a duration histogram and a
/// query counter, each tagged <c>driver=routeros</c>, <c>query=…</c> and <c>outcome=success|fail</c>.
/// </summary>
public sealed class RouterOsMetrics : IDisposable
{
    /// <summary>The meter name to enable when configuring an OpenTelemetry/metrics listener.</summary>
    public const string MeterName = "Caisson.Drivers.MikroTik";

    private readonly Meter _meter;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _queries;

    /// <summary>Creates the meter and its instruments.</summary>
    public RouterOsMetrics()
    {
        _meter = new Meter(MeterName);
        _duration = _meter.CreateHistogram<double>(
            "caisson.routeros.query.duration", unit: "ms", description: "RouterOS discovery query duration.");
        _queries = _meter.CreateCounter<long>(
            "caisson.routeros.query.count", unit: "{query}", description: "RouterOS discovery queries by outcome.");
    }

    /// <summary>Records a successful query of type <paramref name="query"/>.</summary>
    public void RecordSuccess(string query, TimeSpan elapsed) => Record(query, "success", elapsed);

    /// <summary>Records a failed query of type <paramref name="query"/>.</summary>
    public void RecordFailure(string query, TimeSpan elapsed) => Record(query, "fail", elapsed);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    private void Record(string query, string outcome, TimeSpan elapsed)
    {
        var tags = new TagList
        {
            { "driver", "routeros" },
            { "query", query },
            { "outcome", outcome },
        };

        _duration.Record(elapsed.TotalMilliseconds, tags);
        _queries.Add(1, tags);
    }
}
