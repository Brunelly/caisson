using Caisson.Domain.Enums;
using Caisson.Domain.Security;

namespace Caisson.Domain.Drift;

/// <summary>
/// A single expected-vs-actual drift finding within a <see cref="DriftReport"/> (story #64, AC1). Uses a
/// surrogate primary key (<see cref="Id"/>) plus a separately content-hashed, stable
/// <see cref="DriftItemId"/> (<c>Diffing.DeterministicGuid</c> over rack/type/subject/expected/actual) —
/// the unique index is scoped to <c>(DriftReportId, DriftItemId)</c>, NOT globally unique on
/// <see cref="DriftItemId"/>, because that hash deliberately excludes the desired-revision/observed-
/// snapshot identity, so identical drift can legitimately recur across reports (mirrors
/// <c>TopologyEntityDiff</c>'s snapshot-scoped-uniqueness precedent). Like <see cref="DriftReport"/>,
/// this is a mutable, upsertable row — not <c>IAppendOnly</c>/<c>ISnapshotScoped</c> — so recompute can
/// insert/delete rows in place (AC3); an item's own content is immutable in practice because it is
/// exactly what <see cref="DriftItemId"/> was hashed from.
/// </summary>
public sealed class DriftItem
{
    private DriftItem()
    {
        // EF Core materialization constructor.
        SubjectKey = null!;
        Why = null!;
    }

    /// <summary>Creates a drift item row.</summary>
    /// <exception cref="ArgumentException">Thrown when a bounded field is missing or exceeds its bound.</exception>
    public DriftItem(
        Guid id,
        Guid driftReportId,
        Guid driftItemId,
        Guid rackId,
        DriftType driftType,
        DriftSeverity severity,
        bool actionable,
        DriftSubjectType subjectType,
        string subjectKey,
        string? expectedValue,
        string? actualValue,
        string why,
        DateTime createdAtUtc,
        string? detailsJson = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(subjectKey);
        ArgumentException.ThrowIfNullOrEmpty(why);

        if (subjectKey.Length > DriftSchema.MaxSubjectKeyLength)
        {
            throw new ArgumentException(
                $"Subject key exceeds the {DriftSchema.MaxSubjectKeyLength}-character bound.", nameof(subjectKey));
        }

        if (expectedValue is { Length: > 0 } && expectedValue.Length > DriftSchema.MaxExpectedValueLength)
        {
            throw new ArgumentException(
                $"Expected value exceeds the {DriftSchema.MaxExpectedValueLength}-character bound.", nameof(expectedValue));
        }

        if (actualValue is { Length: > 0 } && actualValue.Length > DriftSchema.MaxActualValueLength)
        {
            throw new ArgumentException(
                $"Actual value exceeds the {DriftSchema.MaxActualValueLength}-character bound.", nameof(actualValue));
        }

        // Finding #27-style backstop (mirrors TopologyEntityDiff/TopologyAuditEvent): Why/Details are
        // derived from device-reported and desired-state text, so scrub before the length bound so
        // redaction can never push the value over it.
        var scrubbedWhy = SecretScrubber.Scrub(why)!;
        if (scrubbedWhy.Length > DriftSchema.MaxWhyLength)
        {
            throw new ArgumentException($"Why exceeds the {DriftSchema.MaxWhyLength}-character bound.", nameof(why));
        }

        var scrubbedDetailsJson = SecretScrubber.Scrub(detailsJson);
        if (scrubbedDetailsJson is { Length: > DriftSchema.MaxDetailsJsonLength })
        {
            throw new ArgumentException(
                $"Details JSON exceeds the {DriftSchema.MaxDetailsJsonLength}-character bound.", nameof(detailsJson));
        }

        Id = id;
        DriftReportId = driftReportId;
        DriftItemId = driftItemId;
        RackId = rackId;
        DriftType = driftType;
        Severity = severity;
        Actionable = actionable;
        SubjectType = subjectType;
        SubjectKey = subjectKey;
        ExpectedValue = expectedValue;
        ActualValue = actualValue;
        Why = scrubbedWhy;
        DetailsJson = scrubbedDetailsJson;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>Surrogate primary key.</summary>
    public Guid Id { get; private set; }

    /// <summary>The report this item belongs to.</summary>
    public Guid DriftReportId { get; private set; }

    /// <summary>
    /// The stable, content-hashed identifier of this finding (<c>Diffing.DeterministicGuid</c>). Scoped
    /// uniqueness is <c>(DriftReportId, DriftItemId)</c> — see the type-level remarks.
    /// </summary>
    public Guid DriftItemId { get; private set; }

    /// <summary>The rack this item concerns (denormalized for rack-scoped queries).</summary>
    public Guid RackId { get; private set; }

    /// <summary>The kind of drift this item describes.</summary>
    public DriftType DriftType { get; private set; }

    /// <summary>The deterministic, rule-assigned severity of this item.</summary>
    public DriftSeverity Severity { get; private set; }

    /// <summary>
    /// Whether this item implies a concrete, safe-to-apply change. Always <c>false</c> for
    /// <see cref="DriftType.UnknownTopologyMapping"/> (AC2) — an ambiguous subject may never be
    /// presented as if the correct change were known.
    /// </summary>
    public bool Actionable { get; private set; }

    /// <summary>The kind of entity <see cref="SubjectKey"/> identifies.</summary>
    public DriftSubjectType SubjectType { get; private set; }

    /// <summary>The subject's versioned, natural-key identity (<c>Diffing.DriftSubjectKeys</c>).</summary>
    public string SubjectKey { get; private set; }

    /// <summary>The desired-state value, or <c>null</c> when the subject has no desired counterpart.</summary>
    public string? ExpectedValue { get; private set; }

    /// <summary>The observed value, or <c>null</c> when the subject has no observed counterpart.</summary>
    public string? ActualValue { get; private set; }

    /// <summary>Bounded, secret-scrubbed human-readable explanation of the drift.</summary>
    public string Why { get; private set; }

    /// <summary>Optional bounded <c>jsonb</c> structured detail (e.g. ambiguity candidate ports).</summary>
    public string? DetailsJson { get; private set; }

    /// <summary>When this item was (first) computed.</summary>
    public DateTime CreatedAtUtc { get; private set; }
}
