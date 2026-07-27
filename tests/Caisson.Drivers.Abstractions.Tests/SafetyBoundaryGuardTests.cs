using System.Reflection;
using Caisson.Drivers.Abstractions.ReadOnly;
using FluentAssertions;
using Xunit;

namespace Caisson.Drivers.Abstractions.Tests;

/// <summary>
/// NFR1's automated build-failing guard: every interface in the
/// <see cref="Caisson.Drivers.Abstractions.ReadOnly"/> namespace must expose only read-only methods.
/// If a mutating method is ever added there, this test fails the build.
/// </summary>
public sealed class SafetyBoundaryGuardTests
{
    private static readonly Assembly AbstractionsAssembly = typeof(ISwitchDiscoveryDriver).Assembly;

    // Substrings that would signal a write/configure/power/side-effect method leaking into the
    // read-only boundary.
    private static readonly string[] MutationMarkers =
    {
        "Set", "Update", "Create", "Delete", "Remove", "Add", "Reset", "Reboot", "Restart",
        "PowerCycle", "PowerOn", "PowerOff", "Configure", "Apply", "Write", "Push", "Enable",
        "Disable", "Format", "Erase", "Flash", "Provision", "Cycle", "Power",
    };

    public static IEnumerable<object[]> ReadOnlyInterfaceMethods()
    {
        foreach (var type in AbstractionsAssembly.GetTypes().Where(t => t is { IsInterface: true }
                     && t.Namespace == typeof(ISwitchDiscoveryDriver).Namespace))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                yield return new object[] { type.Name, method.Name };
            }
        }
    }

    [Theory]
    [MemberData(nameof(ReadOnlyInterfaceMethods))]
    public void No_method_name_implies_mutation(string typeName, string methodName)
    {
        MutationMarkers.Should().NotContain(
            marker => methodName.Contains(marker, StringComparison.OrdinalIgnoreCase),
            "{0}.{1} lives in the ReadOnly namespace and must not imply a write/side-effect operation",
            typeName, methodName);
    }

    [Fact]
    public void ReadOnly_namespace_contains_the_expected_driver_interfaces()
    {
        // Guards against the enumeration above silently covering zero types/methods if the
        // namespace or interface names ever change.
        ReadOnlyInterfaceMethods().Should().NotBeEmpty();
    }
}
