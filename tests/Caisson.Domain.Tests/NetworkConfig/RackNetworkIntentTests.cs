using Caisson.Domain.NetworkConfig;
using FluentAssertions;
using Xunit;

namespace Caisson.Domain.Tests.NetworkConfig;

/// <summary>Constructor/Update guards for <see cref="RackNetworkIntent"/> (story #168/#176).</summary>
public sealed class RackNetworkIntentTests
{
    private static RackNetworkIntent NewIntent(string intentJson = "{}", string createdBy = "author@example.com")
        => new(Guid.NewGuid(), Guid.NewGuid(), intentJson, createdBy, DateTime.UtcNow);

    [Fact]
    public void New_intent_stamps_created_and_updated_identically()
    {
        var now = DateTime.UtcNow;
        var intent = new RackNetworkIntent(Guid.NewGuid(), Guid.NewGuid(), "{}", "author@example.com", now);

        intent.CreatedAtUtc.Should().Be(now);
        intent.UpdatedAtUtc.Should().Be(now);
        intent.CreatedBy.Should().Be("author@example.com");
        intent.UpdatedBy.Should().Be("author@example.com");
    }

    [Fact]
    public void Intent_json_is_required()
    {
        var act = () => NewIntent(intentJson: "");

        act.Should().Throw<ArgumentException>().WithParameterName("intentJson");
    }

    [Fact]
    public void Intent_json_over_the_bound_is_rejected()
    {
        var oversized = new string('a', RackNetworkIntent.MaxIntentJsonLength + 1);

        var act = () => NewIntent(intentJson: oversized);

        act.Should().Throw<ArgumentException>().WithParameterName("intentJson");
    }

    [Fact]
    public void Created_by_is_required()
    {
        var act = () => NewIntent(createdBy: "");

        act.Should().Throw<ArgumentException>().WithParameterName("createdBy");
    }

    [Fact]
    public void Created_by_over_the_bound_is_rejected()
    {
        var oversized = new string('a', RackNetworkIntent.MaxActorLength + 1);

        var act = () => NewIntent(createdBy: oversized);

        act.Should().Throw<ArgumentException>().WithParameterName("createdBy");
    }

    [Fact]
    public void Update_replaces_the_payload_and_updated_metadata_without_touching_created_metadata()
    {
        var intent = NewIntent();
        var createdAt = intent.CreatedAtUtc;
        var updatedAt = createdAt.AddMinutes(5);

        intent.Update("{\"vlanCatalogue\":[]}", "editor@example.com", updatedAt);

        intent.IntentJson.Should().Be("{\"vlanCatalogue\":[]}");
        intent.UpdatedBy.Should().Be("editor@example.com");
        intent.UpdatedAtUtc.Should().Be(updatedAt);
        intent.CreatedAtUtc.Should().Be(createdAt);
        intent.CreatedBy.Should().Be("author@example.com");
    }

    [Fact]
    public void Update_rejects_an_empty_payload()
    {
        var intent = NewIntent();

        var act = () => intent.Update("", "editor@example.com", DateTime.UtcNow);

        act.Should().Throw<ArgumentException>().WithParameterName("intentJson");
    }

    [Fact]
    public void Update_rejects_an_empty_actor()
    {
        var intent = NewIntent();

        var act = () => intent.Update("{}", "", DateTime.UtcNow);

        act.Should().Throw<ArgumentException>().WithParameterName("updatedBy");
    }
}
