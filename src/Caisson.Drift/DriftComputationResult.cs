using Caisson.Domain.Enums;

namespace Caisson.Drift;

/// <summary>
/// One computed drift finding — a pure record, NOT the EF entity <c>Caisson.Domain.Drift.DriftItem</c>.
/// <c>DriftComputationService</c> maps each of these onto a persisted <c>DriftItem</c> (assigning the
/// surrogate <c>Id</c>, <c>DriftReportId</c> and <c>CreatedAtUtc</c> the pure engine has no business
/// knowing about).
/// </summary>
public sealed record DriftItemResult(
    Guid DriftItemId,
    DriftType DriftType,
    DriftSeverity Severity,
    bool Actionable,
    DriftSubjectType SubjectType,
    string SubjectKey,
    string? ExpectedValue,
    string? ActualValue,
    string Why,
    string? DetailsJson);

/// <summary>
/// The full output of one <see cref="DriftEngine.Compute"/> call (story #64, AC1). Deterministic for
/// identical inputs (NFR1): the same <see cref="Items"/> in the same order, with the same
/// <see cref="DriftItemResult.DriftItemId"/> values and the same <see cref="CountsBySeverityJson"/>.
/// </summary>
/// <param name="Items">
/// Every computed finding, canonically ordered by <c>(SubjectType, SubjectKey, DriftType)</c> (ordinal)
/// and capped at <c>DriftComputationOptions.MaxItemsPerReport</c> (see <see cref="IsTruncated"/>).
/// </param>
/// <param name="ComputedAtUtc">Echoes the caller-supplied computation timestamp (not hashed into any item id).</param>
/// <param name="CountsBySeverityJson">Deterministic <c>jsonb</c>-ready rollup of <see cref="Items"/> counts by severity, plus a total.</param>
/// <param name="HasAmbiguities">Whether <see cref="Items"/> (after truncation) contains a non-actionable <c>UnknownTopologyMapping</c> item (AC2).</param>
/// <param name="IsTruncated">Whether the engine capped item volume against <c>MaxItemsPerReport</c> rather than returning every finding.</param>
/// <param name="Diagnostics">Human-readable, secret-free notes (e.g. a join-key collision) — never fatal.</param>
public sealed record DriftComputationResult(
    IReadOnlyList<DriftItemResult> Items,
    DateTime ComputedAtUtc,
    string CountsBySeverityJson,
    bool HasAmbiguities,
    bool IsTruncated,
    IReadOnlyList<string> Diagnostics);
