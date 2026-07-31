namespace Caisson.Ingestion.Git.GitHub;

/// <summary>
/// The explicit, defense-in-depth PR-only guardrail (story #172, AC3; NFR4). Enforces server-side, and
/// independently of GitHub branch-protection settings, that a feature branch is never the repository default
/// branch before any write occurs. The default branch is the one discovered from repository metadata (the
/// authoritative source), not merely the configured value, so a misconfigured or drifted default cannot slip
/// a default-branch write past the check.
/// </summary>
public static class PrOnlyGuardrail
{
    /// <summary>
    /// Throws <see cref="PrOnlyGuardrailViolationException"/> if <paramref name="featureBranch"/> is null/empty
    /// or equals <paramref name="defaultBranch"/> (case-insensitive, trimmed of a leading <c>refs/heads/</c>).
    /// Invoke this before creating a branch, committing, or opening a PR.
    /// </summary>
    public static void EnsureNotDefaultBranch(string featureBranch, string defaultBranch)
    {
        ArgumentException.ThrowIfNullOrEmpty(defaultBranch);

        if (string.IsNullOrEmpty(featureBranch))
        {
            throw new PrOnlyGuardrailViolationException(
                "A feature branch is required; refusing to write without an explicit non-default branch.");
        }

        var normalizedFeature = NormalizeRef(featureBranch);
        var normalizedDefault = NormalizeRef(defaultBranch);

        if (string.Equals(normalizedFeature, normalizedDefault, StringComparison.OrdinalIgnoreCase))
        {
            throw new PrOnlyGuardrailViolationException(
                "The requested branch equals the repository default branch. This API only creates feature "
                + "branches and pull requests; direct writes to the default branch are refused.");
        }
    }

    private static string NormalizeRef(string reference)
    {
        var trimmed = reference.Trim();
        const string prefix = "refs/heads/";
        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[prefix.Length..]
            : trimmed;
    }
}
