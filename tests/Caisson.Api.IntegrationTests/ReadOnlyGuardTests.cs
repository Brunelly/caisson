using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// Reflection guard for the read-only safety boundary (NFR1): the API assembly references no driver
/// assembly, and every controller action is GET-only. Runs with no database.
/// </summary>
public sealed class ReadOnlyGuardTests
{
    private static readonly Assembly ApiAssembly = typeof(Program).Assembly;

    [Fact]
    public void Api_references_no_driver_assembly()
    {
        var referenced = ApiAssembly.GetReferencedAssemblies().Select(a => a.Name);

        referenced.Should().NotContain(name => name != null && name.StartsWith("Caisson.Drivers", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_controller_action_is_get_only()
    {
        var controllers = ApiAssembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();

        controllers.Should().NotBeEmpty();

        foreach (var controller in controllers)
        {
            var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName);

            foreach (var action in actions)
            {
                var verbs = action.GetCustomAttributes()
                    .OfType<HttpMethodAttribute>()
                    .ToList();

                verbs.Should().NotBeEmpty(
                    "action {0}.{1} must declare an explicit HTTP verb", controller.Name, action.Name);
                verbs.Should().OnlyContain(
                    attribute => attribute is HttpGetAttribute,
                    "action {0}.{1} must be GET-only (NFR1)", controller.Name, action.Name);
            }
        }
    }
}
