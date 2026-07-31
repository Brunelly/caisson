using Caisson.Domain.Git;
using FluentAssertions;
using Xunit;

namespace Caisson.Domain.Tests.Git;

/// <summary>
/// Unit tests for <see cref="PrBranchNaming"/> (story #172, AC1): the branch format, operator/rack
/// slugification safety, the UTC timestamp shape, and the invariant that a generated branch can never equal a
/// bare default branch.
/// </summary>
public sealed class PrBranchNamingTests
{
    private const string Fingerprint = "1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8d9e0f1a2b";

    [Fact]
    public void Build_matches_the_documented_format()
    {
        var ts = new DateTime(2026, 7, 30, 15, 30, 45, DateTimeKind.Utc);

        var branch = PrBranchNaming.Build("rack-a", "jdoe", Fingerprint, ts);

        branch.Should().Be("caisson/rack-a/op-jdoe/20260730T153045Z-1a2b3c4d5e6f");
    }

    [Fact]
    public void Build_renders_the_timestamp_in_utc_with_a_trailing_z()
    {
        var local = new DateTimeOffset(2026, 7, 30, 10, 30, 45, TimeSpan.FromHours(-5)); // 15:30:45Z

        var branch = PrBranchNaming.Build("rack-a", "jdoe", Fingerprint, local.UtcDateTime);

        branch.Should().Contain("20260730T153045Z-");
    }

    [Theory]
    [InlineData("J.Doe@Example.com", "j-doe-example-com")]
    [InlineData("  spaced  name  ", "spaced-name")]
    [InlineData("MixedCASE", "mixedcase")]
    [InlineData("weird///chars!!!", "weird-chars")]
    [InlineData("Ünïcødé", "n-c-d")]
    [InlineData("", "unknown")]
    [InlineData("!!!", "unknown")]
    public void Slugify_produces_a_git_ref_safe_lowercase_token(string input, string expected)
    {
        PrBranchNaming.Slugify(input).Should().Be(expected);
    }

    [Fact]
    public void Slugify_truncates_over_long_segments()
    {
        var slug = PrBranchNaming.Slugify(new string('a', 200));

        slug.Length.Should().BeLessThanOrEqualTo(PrBranchNaming.MaxSegmentLength);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("master")]
    [InlineData("rack-a")]
    public void Build_never_equals_a_bare_default_branch(string defaultBranch)
    {
        var branch = PrBranchNaming.Build("rack-a", "jdoe", Fingerprint, DateTime.UtcNow);

        branch.Should().NotBe(defaultBranch);
        branch.Should().StartWith("caisson/");
    }

    [Fact]
    public void Build_uses_only_the_leading_fingerprint_characters()
    {
        var branch = PrBranchNaming.Build("rack-a", "jdoe", Fingerprint, DateTime.UtcNow);

        branch.Should().EndWith("-1a2b3c4d5e6f");
    }
}
