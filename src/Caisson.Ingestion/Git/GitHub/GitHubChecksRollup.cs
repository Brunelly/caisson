using System.Text.Json;
using System.Text.Json.Serialization;
using Caisson.Domain.Git;

namespace Caisson.Ingestion.Git.GitHub;

/// <summary>The deterministic rollup of a PR's check runs: an overall conclusion, a failing count, and JSON.</summary>
public sealed record GitHubChecksSummary(
    GitPullRequestChecksConclusion Conclusion,
    int? FailingChecksCount,
    string Json);

/// <summary>
/// A pure, DB-free mapper that summarizes GitHub check runs for a head SHA into a
/// <see cref="GitPullRequestChecksConclusion"/> with <b>worst-wins</b> precedence, a failing-checks count, and a
/// compact JSON rollup (story #173, Task #211a). The rollup is sorted by a stable identity (name, then id) so a
/// reordered GitHub response never produces a false transition, and a truncation indicator is set when GitHub
/// reports more runs than the single 100-per-page request returned (keeping the 1-request check-runs ceiling).
/// </summary>
public static class GitHubChecksRollup
{
    /// <summary>The maximum number of check runs surfaced in the JSON rollup (matches the API's per_page ceiling).</summary>
    public const int MaxCheckRuns = 100;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Summarizes <paramref name="result"/> deterministically. An empty/unusable set yields
    /// <see cref="GitPullRequestChecksConclusion.Unknown"/> with a <c>null</c> failing count.
    /// </summary>
    public static GitHubChecksSummary Summarize(GitHubCheckRunsResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Summarize(result.CheckRuns, result.TotalCount);
    }

    /// <summary>Summarizes an explicit run list + reported total (the total drives the truncation indicator).</summary>
    public static GitHubChecksSummary Summarize(IReadOnlyList<GitHubCheckRun> checkRuns, int totalCount)
    {
        ArgumentNullException.ThrowIfNull(checkRuns);

        // Stable sort by identity so a reordered GitHub response serializes identically (no false transition).
        var ordered = checkRuns
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ThenBy(r => r.Id)
            .ToList();

        var perRun = ordered.Select(MapRun).ToList();

        var conclusion = ordered.Count == 0
            ? GitPullRequestChecksConclusion.Unknown
            : RollUp(perRun);

        int? failingCount = ordered.Count == 0
            ? null
            : perRun.Count(IsFailing);

        var truncated = totalCount > ordered.Count;
        var json = SerializeRollup(ordered, perRun, conclusion, truncated);

        return new GitHubChecksSummary(conclusion, failingCount, json);
    }

    /// <summary>Maps a single GitHub check run to its <see cref="GitPullRequestChecksConclusion"/> per-run bucket.</summary>
    private static GitPullRequestChecksConclusion MapRun(GitHubCheckRun run)
    {
        // A run that has not completed is pending, regardless of any (absent) conclusion.
        if (!string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            return GitPullRequestChecksConclusion.Pending;
        }

        return run.Conclusion?.ToLowerInvariant() switch
        {
            "success" => GitPullRequestChecksConclusion.Success,
            "failure" => GitPullRequestChecksConclusion.Failure,
            "neutral" => GitPullRequestChecksConclusion.Neutral,
            "cancelled" or "canceled" => GitPullRequestChecksConclusion.Cancelled,
            "skipped" => GitPullRequestChecksConclusion.Skipped,
            "timed_out" => GitPullRequestChecksConclusion.TimedOut,
            "action_required" => GitPullRequestChecksConclusion.ActionRequired,
            "stale" => GitPullRequestChecksConclusion.Stale,
            _ => GitPullRequestChecksConclusion.Unknown,
        };
    }

    /// <summary>
    /// Worst-wins precedence: any failure-family conclusion wins (reported specifically), then Stale, then any
    /// Pending, then a benign terminal set (success/neutral/skipped) collapses to Success; otherwise Unknown.
    /// </summary>
    private static GitPullRequestChecksConclusion RollUp(IReadOnlyList<GitPullRequestChecksConclusion> perRun)
    {
        if (perRun.Contains(GitPullRequestChecksConclusion.Failure)) return GitPullRequestChecksConclusion.Failure;
        if (perRun.Contains(GitPullRequestChecksConclusion.TimedOut)) return GitPullRequestChecksConclusion.TimedOut;
        if (perRun.Contains(GitPullRequestChecksConclusion.Cancelled)) return GitPullRequestChecksConclusion.Cancelled;
        if (perRun.Contains(GitPullRequestChecksConclusion.ActionRequired)) return GitPullRequestChecksConclusion.ActionRequired;
        if (perRun.Contains(GitPullRequestChecksConclusion.Stale)) return GitPullRequestChecksConclusion.Stale;
        if (perRun.Contains(GitPullRequestChecksConclusion.Pending)) return GitPullRequestChecksConclusion.Pending;

        // Only benign terminal conclusions (success/neutral/skipped) remain → Success; else nothing usable.
        var benign = perRun.Any(c =>
            c is GitPullRequestChecksConclusion.Success
            or GitPullRequestChecksConclusion.Neutral
            or GitPullRequestChecksConclusion.Skipped);

        return benign ? GitPullRequestChecksConclusion.Success : GitPullRequestChecksConclusion.Unknown;
    }

    private static bool IsFailing(GitPullRequestChecksConclusion conclusion)
        => conclusion is GitPullRequestChecksConclusion.Failure
            or GitPullRequestChecksConclusion.TimedOut
            or GitPullRequestChecksConclusion.Cancelled
            or GitPullRequestChecksConclusion.ActionRequired;

    private static string SerializeRollup(
        IReadOnlyList<GitHubCheckRun> ordered,
        IReadOnlyList<GitPullRequestChecksConclusion> perRun,
        GitPullRequestChecksConclusion conclusion,
        bool truncated)
    {
        var checks = new List<RollupCheck>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var run = ordered[i];
            checks.Add(new RollupCheck(
                run.Name,
                run.Status,
                perRun[i].ToString(),
                run.DetailsUrl,
                run.StartedAt,
                run.CompletedAt));
        }

        var rollup = new Rollup(conclusion.ToString(), checks, truncated ? true : null);
        return JsonSerializer.Serialize(rollup, JsonOptions);
    }

    private sealed record Rollup(
        [property: JsonPropertyName("conclusion")] string Conclusion,
        [property: JsonPropertyName("checks")] IReadOnlyList<RollupCheck> Checks,
        [property: JsonPropertyName("truncated")] bool? Truncated);

    private sealed record RollupCheck(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("conclusion")] string Conclusion,
        [property: JsonPropertyName("detailsUrl")] string? DetailsUrl,
        [property: JsonPropertyName("started")] DateTimeOffset? Started,
        [property: JsonPropertyName("completed")] DateTimeOffset? Completed);
}
