using System.Diagnostics.Metrics;

namespace Caisson.Api.Auditing;

/// <summary>
/// Observability for the Tier 2 (durable-first-N) authorization-denial writer (story #308, ADR 0064).
/// Mirrors <see cref="AuditOutboxMetrics"/>'s shape; registered as a singleton.
/// <para>
/// The only counter here is the one that matters operationally: a first-N denial that could NOT be
/// persisted. <see cref="AuthorizationDenialAuditWriter"/> must swallow that failure (a denial-audit
/// failure must never turn a 403 into a 500 — see ADR 0064), which is exactly why the loss has to be
/// counted rather than merely logged: without a metric to alert on, a database outage silently erases
/// the security signal Tier 2 exists to guarantee.
/// </para>
/// </summary>
public sealed class AuthorizationDenialAuditMetrics : IDisposable
{
    /// <summary>The meter name; subscribe to this via <c>System.Diagnostics.Metrics</c>/OpenTelemetry.</summary>
    public const string MeterName = "Caisson.Api.AuthorizationDenialAudit";

    private readonly Meter _meter;
    private readonly Counter<long> _persistenceFailures;

    public AuthorizationDenialAuditMetrics()
    {
        _meter = new Meter(MeterName);
        _persistenceFailures = _meter.CreateCounter<long>(
            "caisson.authorization_denial_audit.persistence_failures",
            unit: "{denial}",
            description: "Authorization denials whose Tier 2 durable record could not be persisted and was lost (alert on any non-zero rate).");
    }

    /// <summary>Records one denial whose durable Tier 2 record was lost because persistence failed.</summary>
    public void RecordPersistenceFailure() => _persistenceFailures.Add(1);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
