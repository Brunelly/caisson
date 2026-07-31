using Caisson.Domain.NetworkConfig.Preflight;
using FluentAssertions;
using Xunit;

namespace Caisson.Domain.Tests.NetworkConfig.Preflight;

/// <summary>RFC 6901 escaping/build tests for the canonical field-path builder (story #170, Q1).</summary>
public sealed class JsonPointerTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a/b", "a~1b")]
    [InlineData("a~b", "a~0b")]
    [InlineData("a~/b", "a~0~1b")] // '~' must be escaped before '/'.
    [InlineData("1/1/1", "1~11~11")]
    public void Escape_encodes_tilde_and_slash_per_rfc_6901(string token, string expected)
        => JsonPointer.Escape(token).Should().Be(expected);

    [Fact]
    public void Build_prefixes_a_slash_and_joins_escaped_tokens()
        => JsonPointer.Build("portIntents", 5, "accessVlanId").Should().Be("/portIntents/5/accessVlanId");

    [Fact]
    public void Build_escapes_a_token_containing_a_slash()
        => JsonPointer.Build("switches", "sw/1", "port").Should().Be("/switches/sw~11/port");
}
