using Caisson.Api.Security;
using FluentAssertions;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// Finding #16: the JWT authority/audience fail-closed startup guard. Mirrors
/// <c>TestAuthSchemeTests</c>'s coverage of <c>TestAuthStartupGuard</c>.
/// </summary>
public sealed class JwtAuthorityStartupGuardTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Validate_throws_on_an_empty_authority_outside_development_or_testing(string environmentName)
    {
        var environment = new TestHostEnvironment(environmentName);

        var act = () => JwtAuthorityStartupGuard.Validate(environment, authority: "", audience: "api://caisson");

        act.Should().Throw<InvalidOperationException>().WithMessage("*Authority*");
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/common/v2.0")]
    [InlineData("https://login.microsoftonline.com/organizations/v2.0")]
    public void Validate_throws_on_a_multi_tenant_authority_outside_development_or_testing(string authority)
    {
        var environment = new TestHostEnvironment("Production");

        var act = () => JwtAuthorityStartupGuard.Validate(environment, authority, audience: "api://caisson");

        act.Should().Throw<InvalidOperationException>().WithMessage("*multi-tenant*");
    }

    [Fact]
    public void Validate_throws_on_an_empty_audience_outside_development_or_testing()
    {
        var environment = new TestHostEnvironment("Production");

        var act = () => JwtAuthorityStartupGuard.Validate(
            environment, authority: "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0",
            audience: "");

        act.Should().Throw<InvalidOperationException>().WithMessage("*Audience*");
    }

    [Fact]
    public void Validate_does_not_throw_for_a_tenant_specific_authority_and_a_non_empty_audience()
    {
        var environment = new TestHostEnvironment("Production");

        var act = () => JwtAuthorityStartupGuard.Validate(
            environment, authority: "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0",
            audience: "api://caisson");

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Validate_never_throws_under_development_or_testing(string environmentName)
    {
        var environment = new TestHostEnvironment(environmentName);

        var act = () => JwtAuthorityStartupGuard.Validate(environment, authority: "", audience: "");

        act.Should().NotThrow();
    }
}
