using System.Reflection;
using Caisson.Drivers.Abstractions.Mutating;
using Caisson.Drivers.Abstractions.ReadOnly;
using FluentAssertions;
using Xunit;

namespace Caisson.Drivers.Abstractions.Tests;

/// <summary>
/// AC1: the write-capable surface is bounded to a single method and is not reachable from the
/// read-only <see cref="ISwitchDiscoveryDriver"/>/<see cref="ReadOnly"/> namespace. This is the write-side
/// counterpart to <see cref="SafetyBoundaryGuardTests"/>, which continues to pass unmodified — nothing
/// declared in the <see cref="Mutating"/> namespace lives in, or is scanned by, the <see cref="ReadOnly"/>
/// guard.
/// </summary>
public sealed class WriteSafetyBoundaryGuardTests
{
    private static readonly Assembly AbstractionsAssembly = typeof(ISwitchMutatingDriver).Assembly;

    [Fact]
    public void ISwitchMutatingDriver_exposes_exactly_one_bounded_operation()
    {
        // Descriptor is a property (identity metadata, never mutation) and is exempt, mirroring how
        // ISwitchDiscoveryDriver also carries a Descriptor property alongside its query methods.
        var methodNames = typeof(ISwitchMutatingDriver)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ToArray();

        methodNames.Should().BeEquivalentTo(new[] { nameof(ISwitchMutatingDriver.SetAccessVlanAsync) },
            "the write surface must be bounded to a single, explicitly allowed change (NFR1)");
    }

    [Fact]
    public void Mutating_interface_does_not_live_in_the_ReadOnly_namespace()
    {
        typeof(ISwitchMutatingDriver).Namespace.Should().NotBe(
            typeof(ISwitchDiscoveryDriver).Namespace,
            "write-capable methods must only be accessible through a distinct interface/namespace (AC1)");
    }

    [Fact]
    public void ReadOnly_namespace_interfaces_never_reference_the_mutating_interface()
    {
        foreach (var type in AbstractionsAssembly.GetTypes().Where(t => t is { IsInterface: true }
                     && t.Namespace == typeof(ISwitchDiscoveryDriver).Namespace))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                method.ReturnType.Should().NotBe(
                    typeof(ISwitchMutatingDriver),
                    "{0}.{1} lives in the ReadOnly namespace and must not expose the mutating interface",
                    type.Name, method.Name);

                method.GetParameters().Select(p => p.ParameterType).Should().NotContain(
                    typeof(ISwitchMutatingDriver),
                    "{0}.{1} lives in the ReadOnly namespace and must not accept the mutating interface",
                    type.Name, method.Name);
            }
        }
    }
}
