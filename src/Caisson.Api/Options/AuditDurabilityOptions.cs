using System.ComponentModel.DataAnnotations;

namespace Caisson.Api.Options;

/// <summary>
/// Tunables for the three audit durability tiers (story #308, ADR 0064), config-bound under
/// <see cref="SectionName"/>. All values have safe defaults so the feature works out of the box; every
/// value is validated at startup (<c>ValidateDataAnnotations().ValidateOnStart()</c>) so a misconfigured
/// non-positive value fails fast rather than surfacing as a silently-idle dispatcher/flush service.
/// </summary>
public sealed class AuditDurabilityOptions
{
    /// <summary>Configuration section this binds from (<c>Audit</c>).</summary>
    public const string SectionName = "Audit";

    /// <summary>How often the Tier 1 outbox dispatcher polls for claimable rows, in seconds.</summary>
    [Range(1, 300)]
    public int OutboxPollIntervalSeconds { get; set; } = 2;

    /// <summary>Maximum outbox rows claimed and dispatched per tick.</summary>
    [Range(1, 5_000)]
    public int OutboxBatchSize { get; set; } = 100;

    /// <summary>
    /// The lease horizon (seconds): a claimed row is not re-claimable by another dispatcher instance until
    /// this elapses, so a crashed dispatcher never strands a row longer than this.
    /// </summary>
    [Range(1, 3_600)]
    public int OutboxLeaseSeconds { get; set; } = 60;

    /// <summary>Maximum dispatch attempts before a row is marked <see cref="Domain.Auditing.AuditOutboxStatus.Poisoned"/>.</summary>
    [Range(1, 100)]
    public int OutboxMaxAttempts { get; set; } = 5;

    /// <summary>Base delay (seconds) for the exponential backoff applied after a transient dispatch failure.</summary>
    [Range(1, 3_600)]
    public int OutboxRetryBaseDelaySeconds { get; set; } = 5;

    /// <summary>Upper bound (seconds) on a single outbox retry backoff delay.</summary>
    [Range(1, 86_400)]
    public int OutboxRetryMaxDelaySeconds { get; set; } = 300;

    /// <summary>
    /// How many denials per (actor, endpoint, outcome, window) bucket are written durably and immediately
    /// (Tier 2(a)) before overflow is collapsed into the in-memory bounded counter (Tier 2(b)).
    /// </summary>
    [Range(1, 1_000)]
    public int DenialFirstN { get; set; } = 5;

    /// <summary>Length of a denial bucket's time window, in seconds.</summary>
    [Range(1, 86_400)]
    public int DenialWindowSeconds { get; set; } = 300;

    /// <summary>
    /// How often the in-memory overflow accumulator flushes to a durable aggregate row, in seconds — also
    /// the upper bound on how much overflow COUNT an ungraceful crash may lose (ADR 0064's accepted loss).
    /// </summary>
    [Range(1, 3_600)]
    public int DenialFlushIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Maximum number of distinct active (not-yet-expired) overflow accumulator buckets retained in
    /// memory at once — bounds the accumulator's footprint under a multi-principal/multi-endpoint flood.
    /// </summary>
    [Range(1, 1_000_000)]
    public int DenialMaxActiveBuckets { get; set; } = 10_000;
}
