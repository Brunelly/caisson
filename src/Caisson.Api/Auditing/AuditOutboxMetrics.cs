using System.Diagnostics.Metrics;

namespace Caisson.Api.Auditing;

/// <summary>
/// Observability for the Tier 1 audit outbox dispatcher (story #308, ADR 0064): counters for rows
/// claimed/dispatched/retried/poisoned. Mirrors <c>GitPullRequestStatusMetrics</c>'s shape; registered as
/// a singleton.
/// </summary>
public sealed class AuditOutboxMetrics : IDisposable
{
    /// <summary>The meter name; subscribe to this via <c>System.Diagnostics.Metrics</c>/OpenTelemetry.</summary>
    public const string MeterName = "Caisson.Api.AuditOutbox";

    private readonly Meter _meter;
    private readonly Counter<long> _claimed;
    private readonly Counter<long> _dispatched;
    private readonly Counter<long> _retried;
    private readonly Counter<long> _poisoned;

    public AuditOutboxMetrics()
    {
        _meter = new Meter(MeterName);
        _claimed = _meter.CreateCounter<long>(
            "caisson.audit_outbox.rows_claimed", unit: "{row}", description: "Outbox rows claimed by the dispatcher's DB lease per tick.");
        _dispatched = _meter.CreateCounter<long>(
            "caisson.audit_outbox.rows_dispatched", unit: "{row}", description: "Outbox rows successfully projected to topology_audit_event.");
        _retried = _meter.CreateCounter<long>(
            "caisson.audit_outbox.rows_retried", unit: "{row}", description: "Outbox rows released back to Pending after a transient dispatch failure.");
        _poisoned = _meter.CreateCounter<long>(
            "caisson.audit_outbox.rows_poisoned", unit: "{row}", description: "Outbox rows that exhausted OutboxMaxAttempts and were marked Poisoned, tagged by sanitized failure code.");
    }

    /// <summary>Records the number of rows claimed this tick.</summary>
    public void RecordClaimed(int count) => _claimed.Add(count);

    /// <summary>Records one successfully dispatched row.</summary>
    public void RecordDispatched() => _dispatched.Add(1);

    /// <summary>Records one row released for retry.</summary>
    public void RecordRetried() => _retried.Add(1);

    /// <summary>Records one row marked Poisoned, tagged by its sanitized failure code.</summary>
    public void RecordPoisoned(string failureCode) => _poisoned.Add(1, new KeyValuePair<string, object?>("reason", failureCode));

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
