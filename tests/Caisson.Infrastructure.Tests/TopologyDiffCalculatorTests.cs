using System.Text.Json;
using Caisson.Correlation.Input;
using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using Caisson.Domain.ValueObjects;
using Caisson.Drivers.Abstractions.Bmc;
using Caisson.Infrastructure.Persistence.Ingestion;
using FluentAssertions;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>DB-free tests of the pure diff engine (AC2). No database required.</summary>
public sealed class TopologyDiffCalculatorTests
{
    private static readonly Guid RackId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CorrelationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTime At = new(2026, 7, 28, 4, 0, 0, DateTimeKind.Utc);

    private static TopologySnapshot MapFrom(TopologyCorrelationInput observed)
        => TopologySnapshotMapper.Map(
            RackId, IngestionTestData.RunContext(), observed, IngestionTestData.Correlation(),
            new IngestionTestData.SequentialIds().NewId).Snapshot;

    private static TopologyDiffResult Diff(TopologySnapshot? prev, TopologySnapshot curr)
        => TopologyDiffCalculator.Diff(prev, curr, CorrelationId, At, new IngestionTestData.SequentialIds().NewId);

    [Fact]
    public void First_snapshot_diffs_every_entity_as_added()
    {
        var current = MapFrom(IngestionTestData.Observed());

        var result = Diff(prev: null, current);

        result.Diffs.Should().NotBeEmpty();
        result.Diffs.Should().OnlyContain(d => d.ChangeType == ChangeType.Added);
        result.Diffs.Should().OnlyContain(d => d.PreviousSnapshotId == null);
        result.Diffs.Should().Contain(d => d.EntityType == TopologyEntityType.Switch);
        result.Diffs.Should().Contain(d => d.EntityType == TopologyEntityType.Nic);
        result.Diffs.Should().Contain(d => d.EntityType == TopologyEntityType.Lldp);

        using var counts = JsonDocument.Parse(result.ChangeCountsJson);
        counts.RootElement.GetProperty("total").GetProperty("added").GetInt32()
            .Should().Be(result.Diffs.Count);
    }

    [Fact]
    public void Identical_snapshots_produce_no_diffs()
    {
        var previous = MapFrom(IngestionTestData.Observed());
        var current = MapFrom(IngestionTestData.Observed());

        Diff(previous, current).Diffs.Should().BeEmpty();
    }

    [Fact]
    public void Diff_is_idempotent_across_reruns()
    {
        var previous = MapFrom(IngestionTestData.Observed());
        var current = MapFrom(IngestionTestData.Observed());

        var first = Diff(previous, current);
        var second = Diff(previous, current);

        first.Diffs.Select(Key).Should().BeEquivalentTo(second.Diffs.Select(Key));
        first.ChangeCountsJson.Should().Be(second.ChangeCountsJson);
    }

    [Fact]
    public void A_changed_field_is_reported_as_modified_with_old_and_new()
    {
        var previous = MapFrom(IngestionTestData.Observed());
        var current = MapFrom(WithRenamedServer1("node-1-renamed"));

        var result = Diff(previous, current);

        // StableKeys.ForServer prefixes with the server's ExternalDeviceKey (finding #3) — "srv1" here.
        var serverDiff = result.Diffs.Single(d =>
            d.EntityType == TopologyEntityType.Server && d.EntityStableKey == "srv1|uuid-1");
        serverDiff.ChangeType.Should().Be(ChangeType.Modified);
        serverDiff.PreviousSnapshotId.Should().Be(previous.Id);

        using var doc = JsonDocument.Parse(serverDiff.DiffPayloadJson);
        var changed = doc.RootElement.GetProperty("changed").GetProperty("hostname");
        changed.GetProperty("old").GetString().Should().Be("node-1");
        changed.GetProperty("new").GetString().Should().Be("node-1-renamed");
    }

    [Fact]
    public void A_removed_entity_is_reported_as_removed()
    {
        var previous = MapFrom(IngestionTestData.Observed());
        var current = MapFrom(WithoutVlan20());

        var result = Diff(previous, current);

        result.Diffs.Should().Contain(d =>
            d.EntityType == TopologyEntityType.Vlan &&
            d.EntityStableKey == "20" &&
            d.ChangeType == ChangeType.Removed);
    }

    private static string Key(TopologyEntityDiff d) => $"{d.EntityType}|{d.EntityStableKey}|{d.ChangeType}";

    private static TopologyCorrelationInput WithRenamedServer1(string hostname)
    {
        var observed = IngestionTestData.Observed();
        var servers = observed.Servers.Select(s => s.ServerId == "srv1"
            ? s with { System = new BmcSystemInventory(BmcType.Redfish, "10.0.1.1", "uuid-1", hostname) }
            : s).ToList();
        return new TopologyCorrelationInput(observed.Switches, servers);
    }

    private static TopologyCorrelationInput WithoutVlan20()
    {
        var observed = IngestionTestData.Observed();
        var switches = observed.Switches
            .Select(sw => sw with { Vlans = sw.Vlans.Where(v => v.VlanId != 20).ToList() })
            .ToList();
        return new TopologyCorrelationInput(switches, observed.Servers);
    }
}
