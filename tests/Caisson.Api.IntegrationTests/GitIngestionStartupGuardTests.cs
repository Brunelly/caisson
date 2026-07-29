using Caisson.Api.Security;
using Caisson.Ingestion.Security;
using FluentAssertions;
using Xunit;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// Story #62, AC5: the fail-closed startup gate for Git ingestion — a deployment with ingestion enabled
/// and no resolvable webhook secret must never come up. Mirrors <see cref="JwtAuthorityStartupGuardTests"/>.
/// </summary>
public sealed class GitIngestionStartupGuardTests : IDisposable
{
    private readonly string? _previousEnv = Environment.GetEnvironmentVariable(EnvGitIngestionSecretsResolver.SecretEnvVar);
    private readonly string? _previousAspNetEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvGitIngestionSecretsResolver.SecretEnvVar, _previousEnv);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", _previousAspNetEnv);
    }

    [Fact]
    public void Validate_never_throws_when_ingestion_is_disabled()
    {
        Environment.SetEnvironmentVariable(EnvGitIngestionSecretsResolver.SecretEnvVar, null);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
        var environment = new TestHostEnvironment("Production");

        var act = () => GitIngestionStartupGuard.Validate(environment, ingestionEnabled: false, new EnvGitIngestionSecretsResolver());

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_throws_when_enabled_and_no_secret_resolves_in_production()
    {
        Environment.SetEnvironmentVariable(EnvGitIngestionSecretsResolver.SecretEnvVar, null);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
        var environment = new TestHostEnvironment("Production");

        var act = () => GitIngestionStartupGuard.Validate(environment, ingestionEnabled: true, new EnvGitIngestionSecretsResolver());

        act.Should().Throw<InvalidOperationException>().WithMessage("*GitIngestion:Enabled*");
    }

    [Fact]
    public void Validate_does_not_throw_when_enabled_and_a_secret_is_configured()
    {
        Environment.SetEnvironmentVariable(EnvGitIngestionSecretsResolver.SecretEnvVar, "a-real-secret");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
        var environment = new TestHostEnvironment("Production");

        var act = () => GitIngestionStartupGuard.Validate(environment, ingestionEnabled: true, new EnvGitIngestionSecretsResolver());

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_does_not_throw_when_enabled_outside_production_with_no_secret_configured()
    {
        Environment.SetEnvironmentVariable(EnvGitIngestionSecretsResolver.SecretEnvVar, null);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        var environment = new TestHostEnvironment("Development");

        var act = () => GitIngestionStartupGuard.Validate(environment, ingestionEnabled: true, new EnvGitIngestionSecretsResolver());

        act.Should().NotThrow();
    }
}
