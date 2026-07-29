using Caisson.Ingestion.Materializer;
using Caisson.Ingestion.Schema;
using FluentAssertions;
using Xunit;

namespace Caisson.Ingestion.Tests;

/// <summary>
/// Story #63, AC1/AC2: the persisted payload and its content hash must be deterministic — the same
/// validated document always serializes to the same bytes, so repeat ingestion of unchanged content can
/// be detected and the payload is stable for callers to compare/hash.
/// </summary>
public sealed class DesiredStatePayloadSerializerTests
{
    private static ValidatedRackDocument SampleDocument() => new(
        "rack-1",
        new[]
        {
            new ValidatedSwitch(
                "switch-a",
                new[]
                {
                    new ValidatedPort("eth0", 100, "uplink", "leaf-1", "Ethernet1"),
                    new ValidatedPort("eth1", 200, null, null, null),
                }),
        });

    [Fact]
    public void Serialize_produces_byte_identical_output_for_the_same_document_shape()
    {
        var first = DesiredStatePayloadSerializer.Serialize(SampleDocument());
        var second = DesiredStatePayloadSerializer.Serialize(SampleDocument());

        first.Should().Be(second);
    }

    [Fact]
    public void Serialize_uses_camelCase_property_names()
    {
        var json = DesiredStatePayloadSerializer.Serialize(SampleDocument());

        json.Should().Contain("\"rackSlug\"");
        json.Should().Contain("\"switches\"");
        json.Should().Contain("\"accessVlan\"");
        json.Should().NotContain("\"RackSlug\"");
    }

    [Fact]
    public void Serialize_includes_null_optional_fields_explicitly()
    {
        var json = DesiredStatePayloadSerializer.Serialize(SampleDocument());

        json.Should().Contain("\"description\":null");
    }

    [Fact]
    public void Serialize_differs_when_document_content_differs()
    {
        var original = DesiredStatePayloadSerializer.Serialize(SampleDocument());
        var changed = DesiredStatePayloadSerializer.Serialize(new ValidatedRackDocument(
            "rack-1",
            new[] { new ValidatedSwitch("switch-a", new[] { new ValidatedPort("eth0", 999, null, null, null) }) }));

        changed.Should().NotBe(original);
    }
}
