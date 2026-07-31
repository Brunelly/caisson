using System.Globalization;
using System.Text;

namespace Caisson.Domain.Git;

/// <summary>
/// Builds the deterministic, traceable feature-branch name for a rack desired-state PR (story #172, AC1):
/// <c>caisson/{rackSlug}/op-{operatorSlug}/{yyyyMMddTHHmmssZ}-{fingerprint12}</c>, e.g.
/// <c>caisson/rack-a/op-jdoe/20260730T153045Z-1a2b3c4d5e6f</c>. Pure and side-effect free.
/// <para>
/// The <c>rackSlug</c> is the rack's <c>ExternalKey</c> and the <c>operatorSlug</c> is derived from the
/// caller's <c>oid</c>/UPN claim; both are slugified to a git-ref-safe form (lowercase, ASCII
/// alphanumerics and single hyphens only) so invalid characters, Unicode, and over-long identifiers can
/// never produce a ref the GitHub API rejects. The short 12-hex fingerprint suffix keeps distinct
/// candidates submitted within the same second from colliding on a branch name while preserving the
/// human-readable convention from the story. The <c>caisson/</c> multi-segment prefix plus timestamp means
/// the result can never equal a bare default-branch name such as <c>main</c>/<c>master</c>; the
/// authoritative refusal still lives in the PR-only guardrail.
/// </para>
/// </summary>
public static class PrBranchNaming
{
    /// <summary>The fixed branch prefix identifying Caisson-authored desired-state PRs.</summary>
    public const string DefaultPrefix = "caisson";

    /// <summary>Number of leading fingerprint hex characters appended as a collision-avoidance suffix.</summary>
    public const int FingerprintSuffixLength = 12;

    /// <summary>Maximum length of a single slugified segment (rack/operator) before truncation.</summary>
    public const int MaxSegmentLength = 40;

    private const string EmptySegmentFallback = "unknown";

    /// <summary>The UTC timestamp format used in the branch name (e.g. <c>20260730T153045Z</c>).</summary>
    public const string TimestampFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>
    /// Builds the branch name for a candidate. <paramref name="timestampUtc"/> is rendered in UTC;
    /// <paramref name="candidateFingerprint"/> is the lowercase 64-hex candidate fingerprint (only its
    /// leading <see cref="FingerprintSuffixLength"/> characters are used).
    /// </summary>
    public static string Build(
        string rackExternalKey,
        string operatorIdentifier,
        string candidateFingerprint,
        DateTime timestampUtc,
        string prefix = DefaultPrefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(candidateFingerprint);

        var rackSlug = Slugify(rackExternalKey);
        var operatorSlug = Slugify(operatorIdentifier);
        var timestamp = timestampUtc.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var suffix = FingerprintSuffix(candidateFingerprint);

        return $"{prefix}/{rackSlug}/op-{operatorSlug}/{timestamp}-{suffix}";
    }

    /// <summary>
    /// Slugifies an arbitrary identifier to a git-ref-safe, lowercase ASCII-alphanumeric-plus-single-hyphen
    /// token, truncated to <see cref="MaxSegmentLength"/>. Empty/whitespace/all-invalid input yields a
    /// stable fallback so a branch segment is never empty.
    /// </summary>
    public static string Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return EmptySegmentFallback;
        }

        var builder = new StringBuilder(value.Length);
        var lastWasHyphen = false;
        foreach (var ch in value)
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                builder.Append(ch);
                lastWasHyphen = false;
            }
            else if (ch is >= 'A' and <= 'Z')
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen && builder.Length > 0)
            {
                // Any other character (including Unicode and separators) collapses to a single hyphen.
                builder.Append('-');
                lastWasHyphen = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > MaxSegmentLength)
        {
            slug = slug[..MaxSegmentLength].Trim('-');
        }

        return slug.Length == 0 ? EmptySegmentFallback : slug;
    }

    private static string FingerprintSuffix(string candidateFingerprint)
    {
        var trimmed = candidateFingerprint.Trim().ToLowerInvariant();
        return trimmed.Length <= FingerprintSuffixLength
            ? trimmed
            : trimmed[..FingerprintSuffixLength];
    }
}
