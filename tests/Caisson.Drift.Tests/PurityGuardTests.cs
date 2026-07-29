using System.Reflection;
using FluentAssertions;
using Xunit;

namespace Caisson.Drift.Tests;

/// <summary>
/// NFR1's automated build-failing guard: the drift computation engine must be pure and perform no I/O.
/// Enforced statically by asserting the shipped <c>Caisson.Drift</c> assembly references no EF Core, no
/// Npgsql, and no <c>System.Net.Http</c> — mirrors <c>Caisson.Correlation.Tests.PurityGuardTests</c>
/// verbatim, against the new assembly.
/// </summary>
public sealed class PurityGuardTests
{
    private static readonly Assembly DriftAssembly = typeof(DriftEngine).Assembly;

    // Assembly-name substrings that would signal database or network I/O leaking into the pure engine.
    private static readonly string[] ForbiddenReferences =
    {
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "System.Net.Http",
    };

    public static IEnumerable<object[]> ReferencedAssemblies()
    {
        foreach (var reference in DriftAssembly.GetReferencedAssemblies())
        {
            yield return new object[] { reference.Name ?? string.Empty };
        }
    }

    [Theory]
    [MemberData(nameof(ReferencedAssemblies))]
    public void No_referenced_assembly_pulls_in_database_or_http_io(string referenceName)
    {
        ForbiddenReferences.Should().NotContain(
            forbidden => referenceName.StartsWith(forbidden, StringComparison.OrdinalIgnoreCase),
            "Caisson.Drift must stay pure/side-effect free (NFR1); {0} implies I/O", referenceName);
    }

    [Fact]
    public void The_reference_enumeration_is_not_silently_empty()
    {
        // Guards against the theory above vacuously passing if reference discovery ever breaks.
        ReferencedAssemblies().Should().NotBeEmpty();
    }
}
