using Caisson.Domain.NetworkConfig;
using Caisson.Domain.NetworkConfig.Preflight;
using FluentAssertions;
using Xunit;

namespace Caisson.Domain.Tests.NetworkConfig.Preflight;

/// <summary>
/// Stability/mismatch tests for the stateless, content-bound <see cref="ValidationRunToken"/> (story #170,
/// Q3 answer): identical input + topology yields an identical id; any candidate or topology change yields a
/// different id (TOCTOU safety).
/// </summary>
public sealed class ValidationRunTokenTests
{
    private static readonly Guid RackId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Snapshot = new("22222222-2222-2222-2222-222222222222");

    private static readonly VlanCatalogueEntry[] Catalogue =
    {
        new(10, "data", "primary"),
        new(20, "storage", null),
    };

    private static readonly PortAccessIntent[] Intents =
    {
        new("sw1", "ether1", 10),
        new("sw1", "ether2", 20),
    };

    [Fact]
    public void Identical_input_and_topology_yield_an_identical_id()
    {
        var a = ValidationRunToken.Compute(RackId, Catalogue, Intents, Snapshot);
        var b = ValidationRunToken.Compute(RackId, Catalogue, Intents, Snapshot);

        a.Should().Be(b);
        a.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Reordering_equivalent_content_yields_the_same_id()
    {
        var reordered = new[] { Catalogue[1], Catalogue[0] };

        ValidationRunToken.Compute(RackId, reordered, Intents, Snapshot)
            .Should().Be(ValidationRunToken.Compute(RackId, Catalogue, Intents, Snapshot));
    }

    [Fact]
    public void A_candidate_edit_yields_a_different_id()
    {
        var edited = new[] { new VlanCatalogueEntry(10, "data", "primary"), new VlanCatalogueEntry(21, "storage", null) };

        ValidationRunToken.Compute(RackId, edited, Intents, Snapshot)
            .Should().NotBe(ValidationRunToken.Compute(RackId, Catalogue, Intents, Snapshot));
    }

    [Fact]
    public void A_topology_snapshot_change_yields_a_different_id()
    {
        var other = Guid.NewGuid();

        ValidationRunToken.Compute(RackId, Catalogue, Intents, other)
            .Should().NotBe(ValidationRunToken.Compute(RackId, Catalogue, Intents, Snapshot));
    }

    [Fact]
    public void A_null_description_and_an_empty_description_are_distinguished()
    {
        var nullDesc = new[] { new VlanCatalogueEntry(10, "data", null) };
        var emptyDesc = new[] { new VlanCatalogueEntry(10, "data", string.Empty) };

        ValidationRunToken.Compute(RackId, nullDesc, Array.Empty<PortAccessIntent>(), Snapshot)
            .Should().NotBe(ValidationRunToken.Compute(RackId, emptyDesc, Array.Empty<PortAccessIntent>(), Snapshot));
    }

    [Fact]
    public void A_missing_snapshot_is_distinct_from_a_present_one()
    {
        ValidationRunToken.Compute(RackId, Catalogue, Intents, observedSnapshotId: null)
            .Should().NotBe(ValidationRunToken.Compute(RackId, Catalogue, Intents, Snapshot));
    }
}
