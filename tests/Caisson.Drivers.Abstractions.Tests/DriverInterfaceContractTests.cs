using System.Reflection;
using Caisson.Drivers.Abstractions.ReadOnly;
using Caisson.Drivers.Abstractions.Results;
using FluentAssertions;
using Xunit;

namespace Caisson.Drivers.Abstractions.Tests;

/// <summary>
/// AC1/AC3: every discovery method returns <see cref="DriverResult{T}"/> — never a raw protocol
/// object — and accepts exactly one <see cref="CancellationToken"/>, verified by reflection so the
/// contract cannot silently drift as new driver methods are added.
/// </summary>
public sealed class DriverInterfaceContractTests
{
    private static readonly Assembly AbstractionsAssembly = typeof(ISwitchDiscoveryDriver).Assembly;

    public static IEnumerable<object[]> DiscoveryMethods()
    {
        foreach (var type in AbstractionsAssembly.GetTypes().Where(t => t is { IsInterface: true }
                     && t.Namespace == typeof(ISwitchDiscoveryDriver).Namespace))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .Where(m => !m.IsSpecialName))
            {
                yield return new object[] { type.Name, method.Name };
            }
        }
    }

    private static MethodInfo ResolveMethod(string typeName, string methodName)
    {
        var type = AbstractionsAssembly.GetTypes().Single(t => t.Namespace == typeof(ISwitchDiscoveryDriver).Namespace
            && t.Name == typeName);
        return type.GetMethod(methodName)!;
    }

    [Theory]
    [MemberData(nameof(DiscoveryMethods))]
    public void Method_returns_task_of_driver_result(string typeName, string methodName)
    {
        var method = ResolveMethod(typeName, methodName);

        method.ReturnType.IsGenericType.Should().BeTrue(
            "{0}.{1} must return Task<DriverResult<T>>", typeName, methodName);
        method.ReturnType.GetGenericTypeDefinition().Should().Be(typeof(Task<>),
            "{0}.{1} must return a Task", typeName, methodName);

        var taskResultType = method.ReturnType.GetGenericArguments()[0];
        taskResultType.IsGenericType.Should().BeTrue(
            "{0}.{1} must return Task<DriverResult<T>>, not a raw protocol object", typeName, methodName);
        taskResultType.GetGenericTypeDefinition().Should().Be(typeof(DriverResult<>),
            "{0}.{1} must return Task<DriverResult<T>>, not a raw protocol object", typeName, methodName);
    }

    [Theory]
    [MemberData(nameof(DiscoveryMethods))]
    public void Method_accepts_exactly_one_cancellation_token(string typeName, string methodName)
    {
        var parameters = ResolveMethod(typeName, methodName).GetParameters();
        parameters.Should().ContainSingle(
            p => p.ParameterType == typeof(CancellationToken),
            "{0}.{1} must accept exactly one CancellationToken (NFR2)", typeName, methodName);
    }
}
