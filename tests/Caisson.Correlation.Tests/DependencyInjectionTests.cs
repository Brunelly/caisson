using Caisson.Correlation.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caisson.Correlation.Tests;

/// <summary>The engine resolves through the shipped <c>AddTopologyCorrelation()</c> DI extension.</summary>
public sealed class DependencyInjectionTests
{
    [Fact]
    public void AddTopologyCorrelation_registers_the_engine_as_a_resolvable_singleton()
    {
        var provider = new ServiceCollection().AddTopologyCorrelation().BuildServiceProvider();

        var first = provider.GetRequiredService<ITopologyCorrelationEngine>();
        var second = provider.GetRequiredService<ITopologyCorrelationEngine>();

        first.Should().BeOfType<TopologyCorrelationEngine>();
        second.Should().BeSameAs(first, "the engine is stateless and registered as a singleton");
    }

    [Fact]
    public void AddTopologyCorrelation_returns_the_same_collection_for_chaining()
    {
        var services = new ServiceCollection();
        services.AddTopologyCorrelation().Should().BeSameAs(services);
    }

    [Fact]
    public void AddTopologyCorrelation_throws_when_services_is_null()
    {
        var act = () => CorrelationServiceCollectionExtensions.AddTopologyCorrelation(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
