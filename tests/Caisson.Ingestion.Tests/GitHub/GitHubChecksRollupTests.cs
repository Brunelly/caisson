using System.Text.Json;
using Caisson.Domain.Git;
using Caisson.Ingestion.Git.GitHub;
using FluentAssertions;
using Xunit;

namespace Caisson.Ingestion.Tests.GitHub;

/// <summary>
/// Unit tests for <see cref="GitHubChecksRollup"/> (story #173, Task #211a): the worst-wins precedence table,
/// empty→Unknown, in-progress→Pending, the failing count, the stable-sort invariant (a reordered response
/// serializes identically), and the &gt;100 truncation indicator.
/// </summary>
public sealed class GitHubChecksRollupTests
{
    private static GitHubCheckRun Run(string name, string status, string? conclusion, long id = 0)
        => new(id, name, status, conclusion, null, null, null);

    [Fact]
    public void Empty_check_set_is_unknown_with_null_failing_count()
    {
        var summary = GitHubChecksRollup.Summarize(Array.Empty<GitHubCheckRun>(), 0);

        summary.Conclusion.Should().Be(GitPullRequestChecksConclusion.Unknown);
        summary.FailingChecksCount.Should().BeNull();
    }

    [Fact]
    public void All_success_rolls_up_to_success_with_zero_failing()
    {
        var runs = new[]
        {
            Run("build", "completed", "success"),
            Run("test", "completed", "success"),
        };

        var summary = GitHubChecksRollup.Summarize(runs, 2);

        summary.Conclusion.Should().Be(GitPullRequestChecksConclusion.Success);
        summary.FailingChecksCount.Should().Be(0);
    }

    [Fact]
    public void Benign_terminal_set_of_success_neutral_skipped_collapses_to_success()
    {
        var runs = new[]
        {
            Run("a", "completed", "success"),
            Run("b", "completed", "neutral"),
            Run("c", "completed", "skipped"),
        };

        var summary = GitHubChecksRollup.Summarize(runs, 3);

        summary.Conclusion.Should().Be(GitPullRequestChecksConclusion.Success);
    }

    [Fact]
    public void Any_in_progress_rolls_up_to_pending()
    {
        var runs = new[]
        {
            Run("a", "completed", "success"),
            Run("b", "in_progress", null),
        };

        var summary = GitHubChecksRollup.Summarize(runs, 2);

        summary.Conclusion.Should().Be(GitPullRequestChecksConclusion.Pending);
    }

    [Fact]
    public void Queued_is_pending()
    {
        var summary = GitHubChecksRollup.Summarize(new[] { Run("a", "queued", null) }, 1);

        summary.Conclusion.Should().Be(GitPullRequestChecksConclusion.Pending);
    }

    [Theory]
    [InlineData("failure", GitPullRequestChecksConclusion.Failure)]
    [InlineData("timed_out", GitPullRequestChecksConclusion.TimedOut)]
    [InlineData("cancelled", GitPullRequestChecksConclusion.Cancelled)]
    [InlineData("action_required", GitPullRequestChecksConclusion.ActionRequired)]
    public void Failure_family_wins_over_pending_and_success(string conclusion, GitPullRequestChecksConclusion expected)
    {
        var runs = new[]
        {
            Run("a", "completed", "success"),
            Run("b", "in_progress", null),
            Run("c", "completed", conclusion),
        };

        var summary = GitHubChecksRollup.Summarize(runs, 3);

        summary.Conclusion.Should().Be(expected);
        summary.FailingChecksCount.Should().Be(1);
    }

    [Fact]
    public void Worst_wins_across_multiple_failure_conclusions()
    {
        var runs = new[]
        {
            Run("a", "completed", "action_required"),
            Run("b", "completed", "failure"),
            Run("c", "completed", "timed_out"),
        };

        var summary = GitHubChecksRollup.Summarize(runs, 3);

        // Failure outranks timed_out and action_required.
        summary.Conclusion.Should().Be(GitPullRequestChecksConclusion.Failure);
        summary.FailingChecksCount.Should().Be(3);
    }

    [Fact]
    public void Reordered_responses_serialize_identically()
    {
        var a = new[] { Run("z", "completed", "success", 2), Run("a", "completed", "success", 1) };
        var b = new[] { Run("a", "completed", "success", 1), Run("z", "completed", "success", 2) };

        var first = GitHubChecksRollup.Summarize(a, 2);
        var second = GitHubChecksRollup.Summarize(b, 2);

        first.Json.Should().Be(second.Json);
    }

    [Fact]
    public void More_than_the_page_ceiling_is_flagged_truncated()
    {
        var runs = Enumerable.Range(0, GitHubChecksRollup.MaxCheckRuns)
            .Select(i => Run("check-" + i.ToString("D3"), "completed", "success", i))
            .ToList();

        var summary = GitHubChecksRollup.Summarize(runs, GitHubChecksRollup.MaxCheckRuns + 5);

        using var doc = JsonDocument.Parse(summary.Json);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Non_truncated_rollup_omits_the_truncation_flag()
    {
        var summary = GitHubChecksRollup.Summarize(new[] { Run("a", "completed", "success") }, 1);

        using var doc = JsonDocument.Parse(summary.Json);
        doc.RootElement.TryGetProperty("truncated", out _).Should().BeFalse();
    }
}
