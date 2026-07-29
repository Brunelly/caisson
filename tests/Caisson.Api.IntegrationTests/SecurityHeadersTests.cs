using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>Finding #19: the fixed defensive response headers are present on every API response.</summary>
public sealed class SecurityHeadersTests
{
    [Fact]
    public async Task Every_response_carries_the_defensive_security_headers()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AzureAd:Authority", "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0");
            builder.UseSetting("AzureAd:Audience", "api://caisson-test");
        });
        using var client = factory.CreateClient();

        // An anonymous, unauthenticated request — SecurityHeadersMiddleware runs before authentication,
        // so the headers are present regardless of the eventual response status.
        var response = await client.GetAsync("/health/live");

        response.Headers.TryGetValues("X-Content-Type-Options", out var contentTypeOptions).Should().BeTrue();
        contentTypeOptions!.Should().Contain("nosniff");

        response.Headers.TryGetValues("X-Frame-Options", out var frameOptions).Should().BeTrue();
        frameOptions!.Should().Contain("DENY");

        response.Headers.TryGetValues("Referrer-Policy", out var referrerPolicy).Should().BeTrue();
        referrerPolicy!.Should().Contain("no-referrer");

        response.Headers.TryGetValues("Content-Security-Policy", out var csp).Should().BeTrue();
        csp!.Should().Contain(v => v.Contains("default-src 'none'"));
    }
}
