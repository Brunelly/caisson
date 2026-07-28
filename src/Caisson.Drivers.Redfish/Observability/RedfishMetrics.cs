using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Caisson.Drivers.Redfish.Observability;

/// <summary>
/// Discovery metrics for the Redfish driver (NFR6), built on <see cref="Meter"/> from
/// <c>System.Diagnostics.Metrics</c> — a BCL, AOT-safe API that any OpenTelemetry exporter can scrape
/// later without this assembly taking an OpenTelemetry SDK dependency. Emits a duration histogram and a
/// query counter, each tagged <c>driver=redfish</c>, <c>query=…</c>, <c>outcome=success|fail</c> and
/// <c>source=redfish|ipmi</c> so the Redfish-vs-IPMI fallback provenance is visible in telemetry.
/// </summary>
public sealed class RedfishMetrics : IDisposable
{
    /// <summary>The meter name to enable when configuring an OpenTelemetry/metrics listener.</summary>
    public const string MeterName = "Caisson.Drivers.Redfish";

    /// <summary>The <c>source</c> tag value for data obtained over Redfish.</summary>
    public const string SourceRedfish = "redfish";

    /// <summary>The <c>source</c> tag value for data obtained via the IPMI fallback.</summary>
    public const string SourceIpmi = "ipmi";

    private readonly Meter _meter;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _queries;

    /// <summary>Creates the meter and its instruments.</summary>
    public RedfishMetrics()
    {
        _meter = new Meter(MeterName);
        _duration = _meter.CreateHistogram<double>(
            "caisson.redfish.query.duration", unit: "ms", description: "Redfish/IPMI discovery query duration.");
        _queries = _meter.CreateCounter<long>(
            "caisson.redfish.query.count", unit: "{query}", description: "Redfish/IPMI discovery queries by outcome.");
    }

    /// <summary>Records a successful query of type <paramref name="query"/> sourced from <paramref name="source"/>.</summary>
    public void RecordSuccess(string query, string source, TimeSpan elapsed)
        => Record(query, "success", source, elapsed);

    /// <summary>Records a failed query of type <paramref name="query"/> sourced from <paramref name="source"/>.</summary>
    public void RecordFailure(string query, string source, TimeSpan elapsed)
        => Record(query, "fail", source, elapsed);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    private void Record(string query, string outcome, string source, TimeSpan elapsed)
    {
        var tags = new TagList
        {
            { "driver", "redfish" },
            { "query", query },
            { "outcome", outcome },
            { "source", source },
        };

        _duration.Record(elapsed.TotalMilliseconds, tags);
        _queries.Add(1, tags);
    }
}
