using Caisson.Infrastructure.LiveUpdates;
using FluentAssertions;
using Xunit;

namespace Caisson.Infrastructure.Tests;

/// <summary>
/// DB-free tests of <see cref="TopologyEventAuthenticity"/> (finding #2): the HMAC tag appended to every
/// Redis channel message. Mirrors <c>CursorCodecTests</c>'s style — round-trip against the fixed
/// development key this process falls back to (no <c>ASPNETCORE_ENVIRONMENT=Production</c>), plus
/// rejection of every way a channel message can be tampered with or malformed. <see cref="TopologyEventAuthenticity.Verify"/>
/// must never throw — it runs inside a Redis pub/sub fire-and-forget callback.
/// </summary>
public sealed class TopologyEventAuthenticityTests
{
    [Fact]
    public void Sign_then_Verify_round_trips_the_payload()
    {
        var json = TopologyEventSerialization.Serialize(new HeartbeatEvent(DateTimeOffset.UtcNow));

        var signed = TopologyEventAuthenticity.Sign(json);
        var verified = TopologyEventAuthenticity.Verify(signed);

        verified.Should().Be(json);
    }

    [Fact]
    public void Sign_appends_a_64_character_hex_tag()
    {
        var signed = TopologyEventAuthenticity.Sign("{}");

        signed.Should().StartWith("{}");
        signed[2..].Should().HaveLength(64).And.MatchRegex("^[0-9a-f]{64}$");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short")]
    public void Verify_rejects_missing_or_too_short_input(string? signed)
        => TopologyEventAuthenticity.Verify(signed).Should().BeNull();

    [Fact]
    public void Verify_rejects_an_arbitrary_string_longer_than_the_tag()
        // Long enough to split into a (fake) payload + a 64-char tail, but the tail was never a real MAC
        // over that payload.
        => TopologyEventAuthenticity.Verify(new string('a', 70)).Should().BeNull();

    [Fact]
    public void Verify_rejects_a_tampered_payload()
    {
        var signed = TopologyEventAuthenticity.Sign("""{"type":"heartbeat"}""");
        var tampered = "\"tampered\"" + signed[10..];

        TopologyEventAuthenticity.Verify(tampered).Should().BeNull();
    }

    [Fact]
    public void Verify_rejects_a_tampered_tag()
    {
        var signed = TopologyEventAuthenticity.Sign("""{"type":"heartbeat"}""");
        var flipped = signed[^1] == 'a' ? 'b' : 'a';
        var tampered = signed[..^1] + flipped;

        TopologyEventAuthenticity.Verify(tampered).Should().BeNull();
    }

    [Fact]
    public void Verify_rejects_a_payload_signed_with_a_different_tag_appended_to_a_different_payload()
    {
        var signedA = TopologyEventAuthenticity.Sign("""{"type":"a"}""");
        var signedB = TopologyEventAuthenticity.Sign("""{"type":"b"}""");
        var mac = signedB[^64..];

        TopologyEventAuthenticity.Verify(signedA[..^64] + mac).Should().BeNull();
    }
}
