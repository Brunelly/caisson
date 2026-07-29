using System.Text.Json;
using Caisson.Domain.Drift;

namespace Caisson.Api.Contracts;

/// <summary>Maps <see cref="Caisson.Domain.Drift"/> entities onto the API wire contracts. Pure and allocation-light.</summary>
public static class DriftContractMappers
{
    /// <summary>Maps a drift report's metadata, parsing its counts-by-severity rollup.</summary>
    public static DriftReportSummaryDto ToSummary(DriftReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new DriftReportSummaryDto(
            report.Id,
            report.DesiredRevisionId,
            report.ObservedSnapshotId,
            report.ComputedAtUtc,
            report.ComputationVersion,
            report.TotalItems,
            Parse(report.CountsBySeverityJson),
            report.HasAmbiguities,
            report.IsTruncated,
            report.Status.ToString(),
            report.ErrorSummary);
    }

    /// <summary>Maps a drift item, parsing its optional structured details.</summary>
    public static DriftItemDto ToItemDto(DriftItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new DriftItemDto(
            item.DriftItemId,
            item.DriftReportId,
            item.DriftType.ToString(),
            item.Severity.ToString(),
            item.Actionable,
            item.SubjectType.ToString(),
            item.SubjectKey,
            item.ExpectedValue,
            item.ActualValue,
            item.Why,
            ParseOptional(item.DetailsJson),
            item.CreatedAtUtc);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement? ParseOptional(string? json)
        => string.IsNullOrEmpty(json) ? null : Parse(json);
}
