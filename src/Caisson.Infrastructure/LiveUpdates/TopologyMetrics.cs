using System.Diagnostics.Metrics;

namespace Caisson.Infrastructure.LiveUpdates;

/// <summary>
/// Observability for the live-updates pipeline (story #9, NFR4). A single <see cref="Meter"/> owning the
/// counters operators watch: currently-connected hub clients, event publish failures (fail-open Redis
/// faults), and cross-instance relay deliveries. Lives in Infrastructure so the Redis publisher can count
/// publish failures while the API hub/relay count connections and deliveries, all under one meter.
/// Registered as a singleton.
/// </summary>
public sealed class TopologyMetrics : IDisposable
{
    /// <summary>The meter name; subscribe to this via <c>System.Diagnostics.Metrics</c>/OpenTelemetry.</summary>
    public const string MeterName = "Caisson.Realtime";

    private readonly Meter _meter;
    private readonly UpDownCounter<long> _connectedClients;
    private readonly Counter<long> _publishFailures;
    private readonly Counter<long> _relayDeliveries;

    public TopologyMetrics()
    {
        _meter = new Meter(MeterName);
        _connectedClients = _meter.CreateUpDownCounter<long>(
            "caisson.realtime.connected_clients", unit: "{client}", description: "Currently connected topology hub clients.");
        _publishFailures = _meter.CreateCounter<long>(
            "caisson.realtime.publish_failures", unit: "{failure}", description: "Fail-open event publish failures.");
        _relayDeliveries = _meter.CreateCounter<long>(
            "caisson.realtime.relay_deliveries", unit: "{delivery}", description: "Events relayed from the channel to hub groups.");
    }

    /// <summary>Records a hub connect (+1) or disconnect (-1).</summary>
    public void RecordConnection(int delta) => _connectedClients.Add(delta);

    /// <summary>Records a fail-open publish failure.</summary>
    public void RecordPublishFailure() => _publishFailures.Add(1);

    /// <summary>Records a relayed event delivery to a hub group.</summary>
    public void RecordRelayDelivery() => _relayDeliveries.Add(1);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
