using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Caisson.Drift.Tests;

/// <summary>
/// NFR1's golden determinism guarantee: repeated <see cref="DriftEngine.Compute"/> calls on
/// structurally-identical (but freshly, independently constructed) inputs — different surrogate
/// database ids, same natural content — yield identical <see cref="DriftItemResult.DriftItemId"/> values
/// and identical serialized items, including under truncation.
/// </summary>
public sealed class DeterminismTests
{
    [Fact]
    public void Repeated_computation_on_freshly_built_equivalent_inputs_yields_identical_item_ids_and_ordering()
    {
        var rackId = Guid.NewGuid();
        var at = new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);
        var options = new DriftComputationOptions();

        var result1 = DriftEngine.Compute(
            DriftFixtures.Desired(accessVlan: 15), DriftFixtures.Observed(rackId, pvid: 10), rackId, at, options);
        var result2 = DriftEngine.Compute(
            DriftFixtures.Desired(accessVlan: 15), DriftFixtures.Observed(rackId, pvid: 10), rackId, at, options);

        result1.Items.Select(i => i.DriftItemId).Should().Equal(result2.Items.Select(i => i.DriftItemId));
        Serialize(result1.Items).Should().Be(Serialize(result2.Items));
        result1.CountsBySeverityJson.Should().Be(result2.CountsBySeverityJson);
        result1.HasAmbiguities.Should().Be(result2.HasAmbiguities);
        result1.IsTruncated.Should().Be(result2.IsTruncated);
    }

    [Fact]
    public void Repeated_computation_on_the_same_input_instances_is_idempotent()
    {
        var rackId = Guid.NewGuid();
        var desired = DriftFixtures.Desired(accessVlan: 15);
        var observed = DriftFixtures.Observed(rackId, pvid: 10);
        var options = new DriftComputationOptions();
        var at = DateTime.UtcNow;

        var result1 = DriftEngine.Compute(desired, observed, rackId, at, options);
        var result2 = DriftEngine.Compute(desired, observed, rackId, at, options);

        Serialize(result1.Items).Should().Be(Serialize(result2.Items));
    }

    [Fact]
    public void Truncation_stays_deterministic_across_repeated_computations()
    {
        var rackId = Guid.NewGuid();
        var options = new DriftComputationOptions { MaxItemsPerReport = 3 };
        var at = DateTime.UtcNow;

        var result1 = Compute(rackId, options, at);
        var result2 = Compute(rackId, options, at);

        result1.IsTruncated.Should().BeTrue();
        result2.IsTruncated.Should().BeTrue();
        result1.Items.Select(i => i.DriftItemId).Should().Equal(result2.Items.Select(i => i.DriftItemId));
        Serialize(result1.Items).Should().Be(Serialize(result2.Items));
    }

    private static DriftComputationResult Compute(Guid rackId, DriftComputationOptions options, DateTime at)
    {
        var desired = DriftFixtures.Desired(includePort: false);
        var observed = DriftFixtures.Observed(rackId, includePort: false);
        var sw = observed.Switches.Single();
        for (var i = 0; i < 8; i++)
        {
            sw.AddPort(new Caisson.Domain.Topology.SwitchPort(Guid.NewGuid(), sw.Id, rackId, observed.Id, $"ether{i}"));
        }

        return DriftEngine.Compute(desired, observed, rackId, at, options);
    }

    private static string Serialize(IReadOnlyList<DriftItemResult> items)
        => JsonSerializer.Serialize(items.Select(i => new
        {
            i.DriftItemId,
            i.DriftType,
            i.Severity,
            i.Actionable,
            i.SubjectType,
            i.SubjectKey,
            i.ExpectedValue,
            i.ActualValue,
            i.Why,
            i.DetailsJson,
        }));
}
