using Caisson.Domain.Enums;
using Caisson.Domain.Topology;
using FluentAssertions;
using Xunit;

namespace Caisson.Domain.Tests;

public sealed class SnapshotSelectorTests
{
    private static readonly Guid Rack = Guid.NewGuid();

    [Fact]
    public void Latest_returns_the_snapshot_with_the_newest_created_at()
    {
        var older = Snapshot(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = Snapshot(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        SnapshotSelector.Latest(new[] { older, newer }).Should().BeSameAs(newer);
    }

    [Fact]
    public void Latest_breaks_exact_timestamp_ties_by_id_descending()
    {
        var timestamp = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var greaterId = idA.CompareTo(idB) > 0 ? idA : idB;

        var a = Snapshot(timestamp, idA);
        var b = Snapshot(timestamp, idB);

        var latest = SnapshotSelector.Latest(new[] { a, b });

        latest!.Id.Should().Be(greaterId);
    }

    [Fact]
    public void OrderByLatest_orders_newest_first()
    {
        var s1 = Snapshot(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var s2 = Snapshot(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var s3 = Snapshot(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        SnapshotSelector.OrderByLatest(new[] { s1, s2, s3 })
            .Should().ContainInOrder(s2, s3, s1);
    }

    [Fact]
    public void Latest_returns_null_for_an_empty_sequence()
    {
        SnapshotSelector.Latest(Array.Empty<TopologySnapshot>()).Should().BeNull();
    }

    private static TopologySnapshot Snapshot(DateTime createdAtUtc, Guid? id = null)
        => new(
            id ?? Guid.NewGuid(),
            Rack,
            createdAtUtc,
            createdBy: "svc-discovery",
            source: "chr",
            correlationId: Guid.NewGuid(),
            status: SnapshotStatus.Completed);
}
