using Caisson.Infrastructure.LiveUpdates;
using FluentAssertions;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// Finding #30: <see cref="TopologyEventSerialization.Deserialize"/> must answer "not a recognised event"
/// with <c>null</c>, never a throw, for every shape of bad input a Redis channel message could carry —
/// including a payload missing the polymorphic <c>type</c> discriminator, which
/// <c>System.Text.Json</c>'s polymorphic support rejects with <see cref="NotSupportedException"/> rather
/// than the <see cref="System.Text.Json.JsonException"/> the original code only caught.
/// </summary>
public sealed class TopologyEventSerializationTests
{
    [Fact]
    public void Serialize_then_Deserialize_round_trips_a_snapshot_updated_event()
    {
        var @event = new SnapshotUpdatedEvent(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 3,
            new SnapshotSummary(2, 20, 3, 4, 0, 1), DateTimeOffset.UtcNow, 5, Guid.NewGuid());

        var json = TopologyEventSerialization.Serialize(@event);
        var decoded = TopologyEventSerialization.Deserialize(json);

        decoded.Should().BeEquivalentTo(@event);
    }

    [Fact]
    public void Deserialize_returns_null_for_a_payload_missing_the_type_discriminator()
    {
        // No "type" property — System.Text.Json's polymorphic deserializer throws NotSupportedException
        // here, not JsonException. This is the exact bug finding #30 reported: it must not propagate.
        TopologyEventSerialization.Deserialize("""{"rackId":"11111111-1111-1111-1111-111111111111"}""")
            .Should().BeNull();
    }

    [Fact]
    public void Deserialize_returns_null_for_an_unrecognised_type_discriminator()
    {
        TopologyEventSerialization.Deserialize("""{"type":"not-a-real-event","eventId":"11111111-1111-1111-1111-111111111111"}""")
            .Should().BeNull();
    }

    [Fact]
    public void Deserialize_returns_null_for_malformed_json()
        => TopologyEventSerialization.Deserialize("{not json").Should().BeNull();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Deserialize_returns_null_for_empty_or_whitespace_input(string? json)
        => TopologyEventSerialization.Deserialize(json!).Should().BeNull();
}
