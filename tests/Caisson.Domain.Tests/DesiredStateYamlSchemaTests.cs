using Caisson.Domain.DesiredState;
using FluentAssertions;
using Xunit;

namespace Caisson.Domain.Tests;

/// <summary>
/// Story #169 (ADR 0049): the canonical-shape constants are the single source of truth the importer and the
/// renderer's guard tests read. These tests pin the internal consistency of those constants — in particular
/// that the v1-supported port keys are exactly the leading prefix of the reserved <c>PortKeyOrder</c>, so the
/// "reserved tail" (<c>description</c>/<c>neighbor</c>) claim can never silently drift.
/// </summary>
public sealed class DesiredStateYamlSchemaTests
{
    [Fact]
    public void Supported_port_keys_are_the_leading_prefix_of_the_reserved_port_key_order()
    {
        DesiredStateYamlSchema.PortKeyOrder
            .Take(DesiredStateYamlSchema.SupportedPortKeyOrder.Count)
            .Should().Equal(DesiredStateYamlSchema.SupportedPortKeyOrder);
    }

    [Fact]
    public void Reserved_port_keys_are_exactly_description_and_neighbor()
    {
        DesiredStateYamlSchema.PortKeyOrder
            .Skip(DesiredStateYamlSchema.SupportedPortKeyOrder.Count)
            .Should().Equal("description", "neighbor");
    }

    [Fact]
    public void Reserved_neighbor_key_order_is_the_documented_shape()
    {
        DesiredStateYamlSchema.NeighborKeyOrder.Should().Equal("systemName", "portId");
    }
}
