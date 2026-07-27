using Caisson.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace Caisson.Domain.Tests;

public sealed class MacAddressValueTests
{
    private const string Canonical = "001b44113ab7";

    [Theory]
    [InlineData("00:1b:44:11:3a:b7")]   // colon-grouped, lowercase
    [InlineData("00:1B:44:11:3A:B7")]   // colon-grouped, uppercase
    [InlineData("00-1b-44-11-3a-b7")]   // hyphen-grouped
    [InlineData("001b.4411.3ab7")]      // dot-grouped (Cisco style)
    [InlineData("001B44113AB7")]        // bare, uppercase
    [InlineData("001b44113ab7")]        // already canonical
    [InlineData(" 00:1b:44:11:3a:b7 ")] // surrounding whitespace
    public void Parse_normalizes_every_accepted_format_to_the_same_canonical_value(string input)
    {
        var mac = MacAddressValue.Parse(input);

        mac.Value.Should().Be(Canonical);
    }

    [Fact]
    public void Values_parsed_from_different_formats_are_equal()
    {
        var fromColon = MacAddressValue.Parse("00:1B:44:11:3A:B7");
        var fromDot = MacAddressValue.Parse("001b.4411.3ab7");
        var fromBare = MacAddressValue.Parse("001B44113AB7");

        fromColon.Should().Be(fromDot);
        fromDot.Should().Be(fromBare);
        fromColon.GetHashCode().Should().Be(fromBare.GetHashCode());
    }

    [Fact]
    public void ToDisplay_returns_the_colon_grouped_form_and_round_trips()
    {
        var mac = MacAddressValue.Parse(Canonical);

        mac.ToDisplay().Should().Be("00:1b:44:11:3a:b7");
        MacAddressValue.Parse(mac.ToDisplay()).Should().Be(mac);
    }

    [Theory]
    [InlineData("")]                    // empty
    [InlineData("   ")]                 // whitespace only
    [InlineData("001b44113ab")]         // 11 hex — too short
    [InlineData("001b44113ab7a")]       // 13 hex — too long
    [InlineData("001b44113abz")]        // non-hex character
    [InlineData("gg:hh:ii:jj:kk:ll")]   // non-hex groups
    [InlineData("not-a-mac")]           // garbage
    public void TryParse_returns_false_for_invalid_input(string input)
    {
        MacAddressValue.TryParse(input, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_returns_false_for_null()
    {
        MacAddressValue.TryParse(null, out _).Should().BeFalse();
    }

    [Fact]
    public void Parse_throws_for_invalid_input()
    {
        var act = () => MacAddressValue.Parse("nope");

        act.Should().Throw<FormatException>();
    }
}
