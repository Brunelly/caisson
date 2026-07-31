using System.Reflection;
using Caisson.Ingestion.Git.GitHub;
using FluentAssertions;
using Xunit;

namespace Caisson.Ingestion.Tests.GitHub;

/// <summary>
/// Story #172's PR-only safety boundary (NFR4): the GitHub write adapter
/// <see cref="IGitHubPullRequestClient"/> must be <em>structurally</em> incapable of merging, force-pushing,
/// pushing to / updating the default branch, or deleting a branch. This is the inverse of the read-only
/// <c>GitSafetyBoundaryGuardTests</c>: there, no method may imply a write; here, writes are allowed but a
/// fixed set of dangerous verbs is forbidden. If a method whose name matches one of those verbs is ever added
/// to the interface, this test fails the build.
/// </summary>
public sealed class GitHubWriteBoundaryGuardTests
{
    private static readonly string[] ForbiddenMarkers =
    {
        "Merge", "Force", "PushToDefault", "Push", "DeleteBranch", "Delete", "UpdateDefault", "UpdateReference",
    };

    public static IEnumerable<object[]> ClientMethods()
    {
        foreach (var method in typeof(IGitHubPullRequestClient).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            yield return new object[] { method.Name };
        }
    }

    [Theory]
    [MemberData(nameof(ClientMethods))]
    public void No_method_implies_a_merge_force_push_or_default_branch_mutation(string methodName)
    {
        ForbiddenMarkers.Should().NotContain(
            marker => methodName.Contains(marker, StringComparison.OrdinalIgnoreCase),
            "IGitHubPullRequestClient.{0} must not expose a merge/force/push/delete/default-ref mutation "
            + "— the PR-only guardrail is structural (NFR4)",
            methodName);
    }

    [Fact]
    public void Interface_exposes_the_expected_write_surface()
    {
        var names = typeof(IGitHubPullRequestClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToList();

        names.Should().Contain("OpenPullRequestAsync");
        names.Should().Contain("CreateBranchAsync");
        names.Should().Contain("CommitFileAsync");
        names.Should().NotContain(n => n.Contains("Merge", StringComparison.OrdinalIgnoreCase));
    }
}
