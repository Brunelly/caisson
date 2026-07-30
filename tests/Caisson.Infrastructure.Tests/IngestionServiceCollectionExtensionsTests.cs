using System.Collections.Generic;
using Caisson.Infrastructure.DependencyInjection;
using Caisson.Infrastructure.Persistence;
using Caisson.Infrastructure.Persistence.Drift;
using Caisson.Infrastructure.Persistence.Ingestion;
using Caisson.Ingestion.DependencyInjection;
using Caisson.Ingestion.Ingestion;
using Caisson.Ingestion.Options;
using Caisson.Orchestration.DependencyInjection;
using Caisson.Orchestration.Drift;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Regression coverage for the CI-caught DI gap: <c>TopologySnapshotIngestionService</c> and
/// <c>DesiredStateIngestionService</c> both take a constructor dependency on
/// <see cref="IDriftRecomputeSignal"/>, but only <c>AddCaissonDriftComputation</c> registered a
/// fail-open default. Composition roots that skip that extension (the VirtualRack Seeder, the API's
/// read path exercised by <c>TestAuthSchemeTests</c>) failed to resolve the ingestion services at all.
/// These tests build the service graph from each ingestion registration extension alone — no
/// Postgres/CAISSON_TEST_DB needed, since only the DI graph, never an open connection, is exercised.
/// </summary>
public sealed class IngestionServiceCollectionExtensionsTests
{
    private const string ConnectionString = "Host=localhost;Database=caisson_di_registration_test";

    [Fact]
    public void AddCaissonPersistence_alone_resolves_the_topology_ingestion_service_with_a_no_op_drift_signal()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<CaissonDbContext>(o => o.UseNpgsql(ConnectionString));
        services.AddCaissonPersistence();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ITopologySnapshotIngestionService>()
            .Should().NotBeNull();
        provider.GetRequiredService<IDriftRecomputeSignal>()
            .Should().BeOfType<NoOpDriftRecomputeSignal>("no Orchestration drift wiring is registered here");
    }

    [Fact]
    public void AddCaissonGitIngestion_alone_resolves_the_desired_state_ingestion_service()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{GitIngestionOptions.SectionName}:RepoUrl"] = "https://example.invalid/desired-state.git",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<CaissonDbContext>(o => o.UseNpgsql(ConnectionString));
        services.AddCaissonGitIngestion(configuration);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IDesiredStateIngestionService>()
            .Should().NotBeNull();
    }

    [Fact]
    public void AddCaissonDrift_still_overrides_the_no_op_default_when_composed_with_AddCaissonPersistence()
    {
        var configuration = new ConfigurationBuilder().Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<CaissonDbContext>(o => o.UseNpgsql(ConnectionString));
        services.AddCaissonPersistence();
        services.AddCaissonDrift(configuration);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IDriftRecomputeSignal>()
            .Should().BeOfType<DriftRecomputeSignal>(
                "Orchestration's RemoveAll+AddSingleton override must win over the TryAdd no-op default " +
                "regardless of which order the two extensions ran in");
    }
}
