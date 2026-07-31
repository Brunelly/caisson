using Caisson.Domain.Git;
using FluentAssertions;
using Xunit;

namespace Caisson.Domain.Tests.Git;

/// <summary>
/// Unit tests for <see cref="GitPullRequestStatusRecord"/> transition behaviour (story #173, Task #210):
/// <see cref="GitPullRequestStatusRecord.ApplyObservation"/> reports a meaningful transition only on a
/// state/checks-conclusion change (a head-SHA-only change moves <c>UpdatedAtUtc</c> but is not meaningful),
/// and the poll success/failure schedule methods maintain the lease/backoff fields and last-known status.
/// </summary>
public sealed class GitPullRequestStatusRecordTests
{
    private static readonly DateTime T0 = new(2026, 7, 31, 8, 0, 0, DateTimeKind.Utc);

    private static GitPullRequestStatusRecord NewRecord()
        => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "octo", "repo", 42, "https://gh/pr/42", T0);

    [Fact]
    public void New_record_is_open_unknown_and_due_immediately()
    {
        var record = NewRecord();

        record.State.Should().Be(GitPullRequestStatus.Open);
        record.ChecksConclusion.Should().Be(GitPullRequestChecksConclusion.Unknown);
        record.NextPollAfterUtc.Should().Be(T0);
        record.HeadSha.Should().BeNull();
        record.ConsecutivePollFailures.Should().Be(0);
    }

    [Fact]
    public void ApplyObservation_reports_transition_on_state_change()
    {
        var record = NewRecord();

        var meaningful = record.ApplyObservation(
            GitPullRequestStatus.Merged, "sha1", GitPullRequestChecksConclusion.Success, 0, "{}", T0.AddMinutes(1));

        meaningful.Should().BeTrue();
        record.State.Should().Be(GitPullRequestStatus.Merged);
        record.UpdatedAtUtc.Should().Be(T0.AddMinutes(1));
        record.LastCheckedAtUtc.Should().Be(T0.AddMinutes(1));
    }

    [Fact]
    public void ApplyObservation_reports_transition_on_checks_conclusion_change()
    {
        var record = NewRecord();
        record.ApplyObservation(
            GitPullRequestStatus.Open, "sha1", GitPullRequestChecksConclusion.Pending, null, "{}", T0.AddMinutes(1));

        var meaningful = record.ApplyObservation(
            GitPullRequestStatus.Open, "sha1", GitPullRequestChecksConclusion.Success, 0, "{}", T0.AddMinutes(2));

        meaningful.Should().BeTrue();
        record.ChecksConclusion.Should().Be(GitPullRequestChecksConclusion.Success);
    }

    [Fact]
    public void ApplyObservation_is_a_noop_transition_when_state_and_checks_are_unchanged()
    {
        var record = NewRecord();
        record.ApplyObservation(
            GitPullRequestStatus.Open, "sha1", GitPullRequestChecksConclusion.Pending, 1, "{\"a\":1}", T0.AddMinutes(1));

        var meaningful = record.ApplyObservation(
            GitPullRequestStatus.Open, "sha1", GitPullRequestChecksConclusion.Pending, 1, "{\"a\":1}", T0.AddMinutes(2));

        meaningful.Should().BeFalse();
        // No real datum changed, so UpdatedAtUtc stays at the first observation.
        record.UpdatedAtUtc.Should().Be(T0.AddMinutes(1));
        record.LastCheckedAtUtc.Should().Be(T0.AddMinutes(2));
    }

    [Fact]
    public void ApplyObservation_head_sha_only_change_moves_updated_at_but_is_not_a_transition()
    {
        var record = NewRecord();
        record.ApplyObservation(
            GitPullRequestStatus.Open, "sha1", GitPullRequestChecksConclusion.Pending, null, "{}", T0.AddMinutes(1));

        var meaningful = record.ApplyObservation(
            GitPullRequestStatus.Open, "sha2", GitPullRequestChecksConclusion.Pending, null, "{}", T0.AddMinutes(2));

        meaningful.Should().BeFalse();
        record.HeadSha.Should().Be("sha2");
        record.UpdatedAtUtc.Should().Be(T0.AddMinutes(2));
    }

    [Fact]
    public void ApplyObservation_clears_prior_failure_state()
    {
        var record = NewRecord();
        record.RecordPollFailure("CredentialsRejected", T0.AddMinutes(5), T0.AddMinutes(1));
        record.ConsecutivePollFailures.Should().Be(1);

        record.ApplyObservation(
            GitPullRequestStatus.Open, "sha1", GitPullRequestChecksConclusion.Success, 0, "{}", T0.AddMinutes(2));

        record.ConsecutivePollFailures.Should().Be(0);
        record.LastPollFailureReason.Should().BeNull();
    }

    [Fact]
    public void RecordPollFailure_increments_and_preserves_last_known_status()
    {
        var record = NewRecord();
        record.ApplyObservation(
            GitPullRequestStatus.Open, "sha1", GitPullRequestChecksConclusion.Success, 0, "{}", T0.AddMinutes(1));
        var updatedAt = record.UpdatedAtUtc;

        record.RecordPollFailure("RateLimited", T0.AddMinutes(10), T0.AddMinutes(2));

        record.State.Should().Be(GitPullRequestStatus.Open);
        record.ChecksConclusion.Should().Be(GitPullRequestChecksConclusion.Success);
        record.ConsecutivePollFailures.Should().Be(1);
        record.LastPollFailureReason.Should().Be("RateLimited");
        record.NextPollAfterUtc.Should().Be(T0.AddMinutes(10));
        record.LastCheckedAtUtc.Should().Be(T0.AddMinutes(2));
        // A transient failure is not a status transition.
        record.UpdatedAtUtc.Should().Be(updatedAt);
    }

    [Fact]
    public void RecordPollSuccess_schedules_next_poll()
    {
        var record = NewRecord();

        record.RecordPollSuccess(T0.AddMinutes(5));

        record.NextPollAfterUtc.Should().Be(T0.AddMinutes(5));
    }

    [Fact]
    public void ApplyObservation_rejects_over_long_checks_summary()
    {
        var record = NewRecord();
        var tooLong = new string('x', GitPullRequestStatusRecord.MaxChecksSummaryLength + 1);

        var act = () => record.ApplyObservation(
            GitPullRequestStatus.Open, "sha1", GitPullRequestChecksConclusion.Unknown, null, tooLong, T0);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ApplyObservation_rejects_over_long_head_sha()
    {
        var record = NewRecord();
        var tooLong = new string('a', GitPullRequestStatusRecord.MaxHeadShaLength + 1);

        var act = () => record.ApplyObservation(
            GitPullRequestStatus.Open, tooLong, GitPullRequestChecksConclusion.Unknown, null, null, T0);

        act.Should().Throw<ArgumentException>();
    }
}
