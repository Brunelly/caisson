using Caisson.Drivers.MikroTik.Mapping;
using Caisson.Drivers.MikroTik.Parsing;
using Caisson.Drivers.MikroTik.Tests.Fixtures;
using Caisson.Drivers.MikroTik.Transport;
using FluentAssertions;
using Xunit;

namespace Caisson.Drivers.MikroTik.Tests;

/// <summary>
/// AC3 tolerance at the reader/parser level: booleans in every RouterOS spelling, multi-key fallback,
/// whitespace trimming, VLAN range syntax, missing columns degrading to null (never throwing), and the
/// binary word-framing round-trip.
/// </summary>
public sealed class ParsingTests
{
    [Theory]
    [InlineData("yes", true)]
    [InlineData("no", false)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("YES", true)]
    public void GetBool_accepts_every_RouterOS_boolean_spelling(string value, bool expected)
    {
        var record = new RouterOsRecord(RouterOsFixtures.Row(("running", value)));

        record.GetBool("running").Should().Be(expected);
    }

    [Fact]
    public void GetBool_returns_null_for_missing_or_unknown_values_without_throwing()
    {
        var record = new RouterOsRecord(RouterOsFixtures.Row(("running", "maybe")));

        record.GetBool("running").Should().BeNull();
        record.GetBool("does-not-exist").Should().BeNull();
    }

    [Fact]
    public void GetString_falls_back_across_keys_and_trims_whitespace()
    {
        var record = new RouterOsRecord(RouterOsFixtures.Row(("on-interface", "  ether5  ")));

        // First key absent, second present; value is trimmed.
        record.GetString("interface", "on-interface").Should().Be("ether5");
    }

    [Fact]
    public void Missing_or_renamed_column_returns_null_not_an_exception()
    {
        var record = new RouterOsRecord(RouterOsFixtures.Row(("name", "ether1")));

        record.GetString("mac-address", "chassis-id").Should().BeNull();
        record.GetInt("pvid").Should().BeNull();
    }

    [Theory]
    [InlineData("10,20,30-32", new[] { 10, 20, 30, 31, 32 })]
    [InlineData("10 20", new[] { 10, 20 })]
    [InlineData("5,5,5", new[] { 5 })]
    [InlineData("10,abc,20", new[] { 10, 20 })]
    [InlineData("", new int[0])]
    public void ParseVlanIds_expands_ranges_and_skips_bad_fragments(string input, int[] expected)
    {
        RouterOsMappers.ParseVlanIds(input).Should().Equal(expected);
    }

    [Fact]
    public async Task Sentence_framing_round_trips_words_including_multi_byte_length_prefixes()
    {
        // A >127-byte word forces the 2-byte length prefix; mix short and long words plus attributes.
        var longWord = "=comment=" + new string('x', 300);
        var words = new[] { "/interface/print", "=.id=*1", longWord, "!re" };

        using var stream = new MemoryStream();
        await RouterOsSentence.WriteAsync(stream, words, CancellationToken.None);
        stream.Position = 0;
        var readBack = await RouterOsSentence.ReadAsync(stream, CancellationToken.None);

        readBack.Should().Equal(words);
    }
}
