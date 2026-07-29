using System.Reflection;
using Caisson.Ingestion.Git.ReadOnly;
using FluentAssertions;
using Xunit;

namespace Caisson.Ingestion.Tests;

/// <summary>
/// Story #62's safety boundary ("only reads Git and stores results"): every interface in the
/// <see cref="Caisson.Ingestion.Git.ReadOnly"/> namespace must expose only read-only methods. If a
/// mutating method is ever added there, this test fails the build — mirroring
/// <c>Caisson.Drivers.Abstractions.Tests.SafetyBoundaryGuardTests</c> for the driver ReadOnly boundary.
/// </summary>
public sealed class GitSafetyBoundaryGuardTests
{
    private static readonly Assembly IngestionAssembly = typeof(IGitRepositoryProvider).Assembly;

    private static readonly string[] MutationMarkers =
    {
        "Push", "Commit", "Merge", "Write", "Force", "Set", "Update", "Create", "Delete", "Remove",
        "Reset", "Checkout", "Apply", "Rebase",
    };

    // "Commit" is git's noun for a revision (as in Caisson.Ingestion.Git.ReadOnly.GitCommitInfo), not a
    // mutating verb here — this method only reads the branch tip's commit metadata. Reviewed false
    // positive, same allow-list idea as DomainGuardTests.ReviewedNonSecretProperties.
    private static readonly HashSet<string> ReviewedReadOnlyMethods = new(StringComparer.Ordinal)
    {
        "IGitRepositoryProvider.GetLatestCommitAsync",
    };

    public static IEnumerable<object[]> ReadOnlyInterfaceMethods()
    {
        foreach (var type in IngestionAssembly.GetTypes().Where(t => t is { IsInterface: true }
                     && t.Namespace == typeof(IGitRepositoryProvider).Namespace))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                yield return new object[] { type.Name, method.Name };
            }
        }
    }

    [Theory]
    [MemberData(nameof(ReadOnlyInterfaceMethods))]
    public void No_method_name_implies_a_git_write(string typeName, string methodName)
    {
        if (ReviewedReadOnlyMethods.Contains($"{typeName}.{methodName}"))
        {
            return;
        }

        MutationMarkers.Should().NotContain(
            marker => methodName.Contains(marker, StringComparison.OrdinalIgnoreCase),
            "{0}.{1} lives in the ReadOnly namespace and must not imply a write to the Git repository",
            typeName, methodName);
    }

    [Fact]
    public void ReadOnly_namespace_contains_the_expected_git_interfaces()
    {
        ReadOnlyInterfaceMethods().Should().NotBeEmpty();
    }
}
