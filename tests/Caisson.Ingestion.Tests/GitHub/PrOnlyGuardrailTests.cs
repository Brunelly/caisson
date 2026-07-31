using Caisson.Ingestion.Git.GitHub;
using FluentAssertions;
using Xunit;

namespace Caisson.Ingestion.Tests.GitHub;

/// <summary>
/// Unit tests for <see cref="PrOnlyGuardrail"/> (story #172, AC3): a feature branch equal to (or empty
/// relative to) the default branch is refused before any write, independent of casing or a <c>refs/heads/</c>
/// prefix; a distinct feature branch passes.
/// </summary>
public sealed class PrOnlyGuardrailTests
{
    [Fact]
    public void A_distinct_feature_branch_passes()
    {
        var act = () => PrOnlyGuardrail.EnsureNotDefaultBranch("caisson/rack-a/op-jdoe/x", "main");

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("main", "main")]
    [InlineData("Main", "main")]
    [InlineData("refs/heads/main", "main")]
    [InlineData("main", "refs/heads/main")]
    public void A_branch_equal_to_the_default_is_refused(string featureBranch, string defaultBranch)
    {
        var act = () => PrOnlyGuardrail.EnsureNotDefaultBranch(featureBranch, defaultBranch);

        act.Should().Throw<PrOnlyGuardrailViolationException>();
    }

    [Fact]
    public void An_empty_feature_branch_is_refused()
    {
        var act = () => PrOnlyGuardrail.EnsureNotDefaultBranch("", "main");

        act.Should().Throw<PrOnlyGuardrailViolationException>();
    }
}
