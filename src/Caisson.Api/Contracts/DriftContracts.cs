using System.Text.Json;

namespace Caisson.Api.Contracts;

/// <summary>A drift report's metadata (story #64, AC1/AC3/AC5) — never the full item list (see <see cref="PagedResult{T}"/>).</summary>
public sealed record DriftReportSummaryDto(
    Guid DriftReportId,
    Guid DesiredRevisionId,
    Guid ObservedSnapshotId,
    DateTime ComputedAt,
    int ComputationVersion,
    int TotalItems,
    JsonElement CountsBySeverity,
    bool HasAmbiguities,
    bool IsTruncated,
    string Status,
    string? ErrorSummary);

/// <summary>A drift report's summary plus a page of its items (AC1: latest/report-detail responses).</summary>
public sealed record DriftReportDetailDto(DriftReportSummaryDto Report, PagedResult<DriftItemDto> Items);

/// <summary>A single drift finding (AC1/AC2).</summary>
public sealed record DriftItemDto(
    Guid DriftItemId,
    Guid DriftReportId,
    string DriftType,
    string Severity,
    bool Actionable,
    string SubjectType,
    string SubjectKey,
    string? ExpectedValue,
    string? ActualValue,
    string Why,
    JsonElement? Details,
    DateTime CreatedAt);
