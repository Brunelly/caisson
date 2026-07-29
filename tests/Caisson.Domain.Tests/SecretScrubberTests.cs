using Caisson.Domain.Security;
using FluentAssertions;
using Xunit;

namespace Caisson.Domain.Tests;

/// <summary>
/// Finding #27's value-level scrubber, applied before persisting the three free-text columns the
/// property-name guard cannot see into: <c>TopologyAuditEvent.DetailsJson</c>,
/// <c>TopologyEntityDiff.DiffPayloadJson</c> and <c>DiscoveryJob.ErrorMessage</c>.
/// </summary>
public sealed class SecretScrubberTests
{
    [Fact]
    public void Strips_userinfo_from_a_uri()
    {
        var scrubbed = SecretScrubber.Scrub("connect to postgres://admin:hunter2@db.internal:5432/caisson");

        scrubbed.Should().NotContain("hunter2");
        scrubbed.Should().NotContain("admin:hunter2");
        scrubbed.Should().Contain("postgres://[REDACTED]@db.internal:5432/caisson");
    }

    [Fact]
    public void Redacts_an_authorization_header()
    {
        var scrubbed = SecretScrubber.Scrub("request failed, sent Authorization: Bearer eyJhbGciOiJSUzI1NiJ9.abc.def");

        scrubbed.Should().NotContain("eyJhbGciOiJSUzI1NiJ9");
        scrubbed.Should().Contain("Authorization: [REDACTED]");
    }

    [Theory]
    [InlineData("callback?token=abc123&x=1", "abc123")]
    [InlineData("login?password=s3cret!", "s3cret!")]
    [InlineData("cfg secret=topsecretvalue", "topsecretvalue")]
    [InlineData("x-api-key=AKIA1234567890", "AKIA1234567890")]
    public void Redacts_secret_shaped_query_params(string input, string secretValue)
    {
        var scrubbed = SecretScrubber.Scrub(input);

        scrubbed.Should().NotContain(secretValue);
        scrubbed.Should().Contain("[REDACTED]");
    }

    [Fact]
    public void Redacts_a_pem_block()
    {
        const string pem = """
            -----BEGIN PRIVATE KEY-----
            MIIBVQIBADANBgkqhkiG9w0BAQEFAASCAT8wggE7AgEAAkEA
            -----END PRIVATE KEY-----
            """;

        var scrubbed = SecretScrubber.Scrub($"driver error: {pem}");

        scrubbed.Should().NotContain("MIIBVQIBADANBgkqhkiG9w0BAQEFAASCAT8wggE7AgEAAkEA");
        scrubbed.Should().Contain("[REDACTED PEM BLOCK]");
    }

    [Fact]
    public void Leaves_ordinary_text_untouched()
    {
        const string message = "The RouterOS device did not respond within the timeout.";

        SecretScrubber.Scrub(message).Should().Be(message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Passes_through_null_or_empty(string? value)
        => SecretScrubber.Scrub(value).Should().Be(value);
}
