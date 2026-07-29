using Caisson.Domain.Enums;
using Caisson.Domain.Security;

namespace Caisson.Domain.Drift;

/// <summary>
/// A rack's computed drift between one desired-state revision and one observed topology snapshot (story
/// #64, AC1/AC3). Unlike the observed-state model's append-only entities, this is a mutable,
/// upsertable registry row — the same shape as <c>Discovery.DiscoveryJob</c> — keyed by the unique
/// <c>(RackId, DesiredRevisionId, ObservedSnapshotId)</c> tuple so a recompute for the identical tuple
/// updates this row in place rather than inserting a duplicate (AC3). It deliberately implements
/// NEITHER <c>IAppendOnly</c> nor <c>ISnapshotScoped</c>, so <c>CaissonDbContext.GuardAppendOnly()</c>
/// never blocks the in-place update this story requires; the drift computation audit trail stays
/// append-only via a separate <c>TopologyAuditEvent</c> row written alongside every (re)compute.
/// </summary>
public sealed class DriftReport
{
    private DriftReport()
    {
        // EF Core materialization constructor.
        CountsBySeverityJson = null!;
    }

    /// <summary>Creates a new drift report row for a (rack, desired revision, observed snapshot) tuple.</summary>
    /// <exception cref="ArgumentException">Thrown when a bounded field exceeds its bound.</exception>
    public DriftReport(
        Guid id,
        Guid rackId,
        Guid desiredRevisionId,
        Guid observedSnapshotId,
        DateTime computedAtUtc,
        int computationVersion,
        int totalItems,
        string countsBySeverityJson,
        bool hasAmbiguities,
        bool isTruncated,
        DriftComputationStatus status,
        string? errorSummary = null)
    {
        Id = id;
        RackId = rackId;
        DesiredRevisionId = desiredRevisionId;
        ObservedSnapshotId = observedSnapshotId;
        CountsBySeverityJson = BoundCounts(countsBySeverityJson);
        ApplyOutcome(computedAtUtc, computationVersion, totalItems, hasAmbiguities, isTruncated, status, errorSummary);
    }

    /// <summary>Primary key.</summary>
    public Guid Id { get; private set; }

    /// <summary>The rack this report was computed for.</summary>
    public Guid RackId { get; private set; }

    /// <summary>The desired-state revision (<c>DesiredStateVersion.Id</c>) this report was computed against.</summary>
    public Guid DesiredRevisionId { get; private set; }

    /// <summary>The observed topology snapshot (<c>TopologySnapshot.Id</c>) this report was computed against.</summary>
    public Guid ObservedSnapshotId { get; private set; }

    /// <summary>When this report was last (re)computed.</summary>
    public DateTime ComputedAtUtc { get; private set; }

    /// <summary>The drift engine's rule-set version that produced this report (<see cref="DriftSchema.CurrentComputationVersion"/>).</summary>
    public int ComputationVersion { get; private set; }

    /// <summary>Total number of <see cref="DriftItem"/> rows this report carries.</summary>
    public int TotalItems { get; private set; }

    /// <summary>Bounded <c>jsonb</c> rollup of item counts by <see cref="DriftSeverity"/>.</summary>
    public string CountsBySeverityJson { get; private set; }

    /// <summary>Whether any item in this report is a non-actionable ambiguity (AC2).</summary>
    public bool HasAmbiguities { get; private set; }

    /// <summary>Whether the engine capped item volume against <c>DriftComputationOptions.MaxItemsPerReport</c>.</summary>
    public bool IsTruncated { get; private set; }

    /// <summary>The terminal outcome of the (re)computation that last touched this row.</summary>
    public DriftComputationStatus Status { get; private set; }

    /// <summary>Operator-safe error summary when <see cref="Status"/> is <see cref="DriftComputationStatus.Failed"/>.</summary>
    public string? ErrorSummary { get; private set; }

    /// <summary>
    /// Updates this row's scalar fields in place for a successful recompute of the SAME
    /// <c>(RackId, DesiredRevisionId, ObservedSnapshotId)</c> tuple (AC3) — the identity fields never
    /// change; only the computed outcome does.
    /// </summary>
    public void RecordRecomputation(
        DateTime computedAtUtc, int computationVersion, int totalItems, string countsBySeverityJson,
        bool hasAmbiguities, bool isTruncated)
    {
        CountsBySeverityJson = BoundCounts(countsBySeverityJson);
        ApplyOutcome(
            computedAtUtc, computationVersion, totalItems, hasAmbiguities, isTruncated,
            DriftComputationStatus.Succeeded, errorSummary: null);
    }

    /// <summary>Records a failed (re)computation attempt for this tuple; no items are attached.</summary>
    public void RecordFailure(DateTime computedAtUtc, int computationVersion, string errorSummary)
    {
        CountsBySeverityJson = BoundCounts("{}");
        ApplyOutcome(
            computedAtUtc, computationVersion, totalItems: 0, hasAmbiguities: false, isTruncated: false,
            DriftComputationStatus.Failed, errorSummary);
    }

    private void ApplyOutcome(
        DateTime computedAtUtc, int computationVersion, int totalItems, bool hasAmbiguities, bool isTruncated,
        DriftComputationStatus status, string? errorSummary)
    {
        ComputedAtUtc = computedAtUtc;
        ComputationVersion = computationVersion;
        TotalItems = totalItems;
        HasAmbiguities = hasAmbiguities;
        IsTruncated = isTruncated;
        Status = status;
        ErrorSummary = BoundErrorSummary(errorSummary);
    }

    private static string BoundCounts(string countsBySeverityJson)
    {
        ArgumentNullException.ThrowIfNull(countsBySeverityJson);
        var scrubbed = SecretScrubber.Scrub(countsBySeverityJson)!;
        if (scrubbed.Length > DriftSchema.MaxCountsBySeverityJsonLength)
        {
            throw new ArgumentException(
                $"Counts-by-severity JSON exceeds the {DriftSchema.MaxCountsBySeverityJsonLength}-character bound.",
                nameof(countsBySeverityJson));
        }

        return scrubbed;
    }

    /// <summary>
    /// Unlike <see cref="BoundCounts"/> (and every bounded field in <see cref="DriftItem"/>'s
    /// constructor), an over-length summary is truncated here rather than thrown: this value is an
    /// operator-facing capture of an arbitrary exception message from an already-failed (re)computation
    /// (<see cref="RecordFailure"/>), so the one call site that persists it is failure handling itself —
    /// throwing here would turn "record that computation failed" into a second failure instead of a
    /// best-effort diagnostic, and would risk repeatedly failing at that same recovery step.
    /// </summary>
    private static string? BoundErrorSummary(string? errorSummary)
    {
        var scrubbed = SecretScrubber.Scrub(errorSummary);
        if (scrubbed is { Length: > DriftSchema.MaxErrorSummaryLength })
        {
            return scrubbed[..DriftSchema.MaxErrorSummaryLength];
        }

        return scrubbed;
    }
}
