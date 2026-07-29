using Caisson.Domain.Drift;
using Caisson.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace Caisson.Domain.Tests;

/// <summary>
/// Constructor guards, bounds, and <see cref="Caisson.Domain.Security.SecretScrubber"/> application for
/// <see cref="DriftReport"/>/<see cref="DriftItem"/> (story #64) — mirrors
/// <see cref="DesiredStateVersionTests"/>'s per-field bound-rejection style.
/// </summary>
public sealed class DriftGuardTests
{
    private static DriftReport NewReport(string countsBySeverityJson = "{}", string? errorSummary = null, DriftComputationStatus status = DriftComputationStatus.Succeeded)
        => new(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow,
            DriftSchema.CurrentComputationVersion, totalItems: 0, countsBySeverityJson,
            hasAmbiguities: false, isTruncated: false, status, errorSummary);

    private static DriftItem NewItem(
        string subjectKey = "v1|rack|sw1|ether1", string why = "why", string? detailsJson = null,
        string? expectedValue = "10", string? actualValue = "20")
        => new(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DriftType.AccessVlanMismatch,
            DriftSeverity.High, actionable: true, DriftSubjectType.SwitchPort, subjectKey, expectedValue, actualValue, why,
            DateTime.UtcNow, detailsJson);

    [Fact]
    public void DriftReport_counts_by_severity_json_over_the_bound_is_rejected()
    {
        var oversized = new string('a', DriftSchema.MaxCountsBySeverityJsonLength + 1);

        var act = () => NewReport(countsBySeverityJson: oversized);

        act.Should().Throw<ArgumentException>().WithParameterName("countsBySeverityJson");
    }

    [Fact]
    public void DriftReport_error_summary_over_the_bound_is_truncated_not_rejected()
    {
        // Mirrors DiscoveryJob.Fail's Truncate helper: an operator-safe summary is capped, not thrown.
        var oversized = new string('a', DriftSchema.MaxErrorSummaryLength + 100);

        var report = NewReport(errorSummary: oversized, status: DriftComputationStatus.Failed);

        report.ErrorSummary.Should().HaveLength(DriftSchema.MaxErrorSummaryLength);
    }

    [Fact]
    public void DriftReport_error_summary_is_secret_scrubbed()
    {
        var report = NewReport(
            errorSummary: "failed: postgres://admin:hunter2@db.internal:5432/caisson",
            status: DriftComputationStatus.Failed);

        report.ErrorSummary.Should().NotContain("hunter2");
        report.ErrorSummary.Should().Contain("[REDACTED]");
    }

    [Fact]
    public void DriftReport_recompute_updates_scalar_fields_without_changing_identity()
    {
        var report = NewReport();
        var (rackId, revisionId, snapshotId) = (report.RackId, report.DesiredRevisionId, report.ObservedSnapshotId);

        var newComputedAt = report.ComputedAtUtc.AddMinutes(5);
        report.RecordRecomputation(newComputedAt, DriftSchema.CurrentComputationVersion, totalItems: 3, "{\"total\":3}", hasAmbiguities: true, isTruncated: false);

        report.RackId.Should().Be(rackId);
        report.DesiredRevisionId.Should().Be(revisionId);
        report.ObservedSnapshotId.Should().Be(snapshotId);
        report.ComputedAtUtc.Should().Be(newComputedAt);
        report.TotalItems.Should().Be(3);
        report.HasAmbiguities.Should().BeTrue();
        report.Status.Should().Be(DriftComputationStatus.Succeeded);
    }

    [Fact]
    public void DriftReport_failure_clears_item_counts_and_records_the_error()
    {
        var report = NewReport();

        report.RecordFailure(DateTime.UtcNow, DriftSchema.CurrentComputationVersion, "boom");

        report.Status.Should().Be(DriftComputationStatus.Failed);
        report.TotalItems.Should().Be(0);
        report.ErrorSummary.Should().Be("boom");
    }

    [Fact]
    public void DriftItem_subject_key_is_required()
    {
        var act = () => NewItem(subjectKey: "");

        act.Should().Throw<ArgumentException>().WithParameterName("subjectKey");
    }

    [Fact]
    public void DriftItem_subject_key_over_the_bound_is_rejected()
    {
        var oversized = new string('a', DriftSchema.MaxSubjectKeyLength + 1);

        var act = () => NewItem(subjectKey: oversized);

        act.Should().Throw<ArgumentException>().WithParameterName("subjectKey");
    }

    [Fact]
    public void DriftItem_why_over_the_bound_is_rejected()
    {
        var oversized = new string('a', DriftSchema.MaxWhyLength + 1);

        var act = () => NewItem(why: oversized);

        act.Should().Throw<ArgumentException>().WithParameterName("why");
    }

    [Fact]
    public void DriftItem_details_json_over_the_bound_is_rejected()
    {
        var oversized = new string('a', DriftSchema.MaxDetailsJsonLength + 1);

        var act = () => NewItem(detailsJson: oversized);

        act.Should().Throw<ArgumentException>().WithParameterName("detailsJson");
    }

    [Fact]
    public void DriftItem_expected_value_over_the_bound_is_rejected()
    {
        var oversized = new string('a', DriftSchema.MaxExpectedValueLength + 1);

        var act = () => NewItem(expectedValue: oversized);

        act.Should().Throw<ArgumentException>().WithParameterName("expectedValue");
    }

    [Fact]
    public void DriftItem_actual_value_over_the_bound_is_rejected()
    {
        var oversized = new string('a', DriftSchema.MaxActualValueLength + 1);

        var act = () => NewItem(actualValue: oversized);

        act.Should().Throw<ArgumentException>().WithParameterName("actualValue");
    }

    [Fact]
    public void DriftItem_expected_value_is_secret_scrubbed()
    {
        var item = NewItem(expectedValue: "postgres://admin:hunter2@db.internal:5432/caisson");

        item.ExpectedValue.Should().NotContain("hunter2");
        item.ExpectedValue.Should().Contain("[REDACTED]");
    }

    [Fact]
    public void DriftItem_actual_value_is_secret_scrubbed()
    {
        var item = NewItem(actualValue: "Authorization: Bearer eyJhbGciOiJSUzI1NiJ9.abc.def");

        item.ActualValue.Should().NotContain("eyJhbGciOiJSUzI1NiJ9");
        item.ActualValue.Should().Contain("[REDACTED]");
    }

    [Fact]
    public void DriftItem_why_is_secret_scrubbed()
    {
        var item = NewItem(why: "device reported Authorization: Bearer eyJhbGciOiJSUzI1NiJ9.abc.def");

        item.Why.Should().NotContain("eyJhbGciOiJSUzI1NiJ9");
        item.Why.Should().Contain("[REDACTED]");
    }

    [Fact]
    public void DriftItem_details_json_is_secret_scrubbed()
    {
        var item = NewItem(detailsJson: "{\"note\":\"postgres://admin:hunter2@db.internal:5432/caisson\"}");

        item.DetailsJson.Should().NotContain("hunter2");
        item.DetailsJson.Should().Contain("[REDACTED]");
    }

    [Fact]
    public void DriftItem_details_json_is_optional()
    {
        var item = NewItem(detailsJson: null);

        item.DetailsJson.Should().BeNull();
    }
}
