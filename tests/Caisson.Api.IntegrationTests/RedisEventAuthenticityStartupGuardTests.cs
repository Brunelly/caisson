using Caisson.Api.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// Finding #2: the fail-closed guard on the Redis connection backing live updates. Mirrors
/// <c>JwtAuthorityStartupGuardTests</c>'s coverage of <c>JwtAuthorityStartupGuard</c>.
/// </summary>
public sealed class RedisEventAuthenticityStartupGuardTests
{
    private static IConfiguration ConfigWithRedis(string connectionString)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Redis"] = connectionString,
            })
            .Build();

    [Fact]
    public void Validate_throws_when_redis_has_neither_password_nor_tls_outside_development_or_testing()
    {
        var environment = new TestHostEnvironment("Production");
        var configuration = ConfigWithRedis("localhost:6379");

        var act = () => RedisEventAuthenticityStartupGuard.Validate(environment, configuration);

        act.Should().Throw<InvalidOperationException>().WithMessage("*password*nor*TLS*");
    }

    [Fact]
    public void Validate_does_not_throw_when_redis_has_a_password()
    {
        var environment = new TestHostEnvironment("Production");
        var configuration = ConfigWithRedis("localhost:6379,password=s3cret");

        var act = () => RedisEventAuthenticityStartupGuard.Validate(environment, configuration);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_does_not_throw_when_redis_has_tls()
    {
        var environment = new TestHostEnvironment("Production");
        var configuration = ConfigWithRedis("localhost:6380,ssl=true");

        var act = () => RedisEventAuthenticityStartupGuard.Validate(environment, configuration);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_does_not_throw_when_redis_is_not_configured_at_all()
    {
        var environment = new TestHostEnvironment("Production");
        var configuration = new ConfigurationBuilder().Build();

        var act = () => RedisEventAuthenticityStartupGuard.Validate(environment, configuration);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Validate_never_throws_under_development_or_testing(string environmentName)
    {
        var environment = new TestHostEnvironment(environmentName);
        var configuration = ConfigWithRedis("localhost:6379");

        var act = () => RedisEventAuthenticityStartupGuard.Validate(environment, configuration);

        act.Should().NotThrow();
    }
}
