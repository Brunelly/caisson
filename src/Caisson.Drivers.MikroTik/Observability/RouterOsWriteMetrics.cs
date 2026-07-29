using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Caisson.Drivers.MikroTik.Observability;

/// <summary>
/// Write-path metrics for the RouterOS mutating driver (NFR6/AC6), built on <see cref="Meter"/> —
/// mirroring the shape of <see cref="RouterOsMetrics"/> but a distinct meter/instrument set so read and
/// write operation counts/durations are never conflated. Emits a duration histogram and an operation
/// counter, each tagged <c>driver=routeros</c>, <c>operation=…</c> and
/// <c>outcome=applied|noop|rolledback|failed</c>.
/// </summary>
public sealed class RouterOsWriteMetrics : IDisposable
{
    /// <summary>The meter name to enable when configuring an OpenTelemetry/metrics listener.</summary>
    public const string MeterName = "Caisson.Drivers.MikroTik.Write";

    private readonly Meter _meter;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _operations;

    /// <summary>Creates the meter and its instruments.</summary>
    public RouterOsWriteMetrics()
    {
        _meter = new Meter(MeterName);
        _duration = _meter.CreateHistogram<double>(
            "caisson.routeros.write.duration", unit: "ms", description: "RouterOS write operation duration.");
        _operations = _meter.CreateCounter<long>(
            "caisson.routeros.write.count", unit: "{operation}", description: "RouterOS write operations by outcome.");
    }

    /// <summary>Records a completed operation with an explicit domain outcome (<c>applied</c>, <c>noop</c>, <c>rolledback</c>, ...).</summary>
    public void RecordOutcome(string operation, string outcome, TimeSpan elapsed) => Record(operation, outcome, elapsed);

    /// <summary>Records an infrastructure-level failure (connect/auth/timeout).</summary>
    public void RecordFailure(string operation, TimeSpan elapsed) => Record(operation, "failed", elapsed);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    private void Record(string operation, string outcome, TimeSpan elapsed)
    {
        var tags = new TagList
        {
            { "driver", "routeros" },
            { "operation", operation },
            { "outcome", outcome },
        };

        _duration.Record(elapsed.TotalMilliseconds, tags);
        _operations.Add(1, tags);
    }
}
