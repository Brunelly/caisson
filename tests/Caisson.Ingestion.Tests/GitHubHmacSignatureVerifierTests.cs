using System.Security.Cryptography;
using System.Text;
using Caisson.Ingestion.Security;
using Caisson.Ingestion.Webhook;
using FluentAssertions;
using Xunit;

namespace Caisson.Ingestion.Tests;

/// <summary>Story #62, NFR1: valid/tampered-body/wrong-secret/missing-header webhook signature cases.</summary>
public sealed class GitHubHmacSignatureVerifierTests : IDisposable
{
    private const string Secret = "test-webhook-secret";
    private readonly string? _previousEnv = Environment.GetEnvironmentVariable(EnvGitIngestionSecretsResolver.SecretEnvVar);

    public GitHubHmacSignatureVerifierTests()
        => Environment.SetEnvironmentVariable(EnvGitIngestionSecretsResolver.SecretEnvVar, Secret);

    public void Dispose()
        => Environment.SetEnvironmentVariable(EnvGitIngestionSecretsResolver.SecretEnvVar, _previousEnv);

    private static string Sign(string secret, byte[] body)
        => "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

    [Fact]
    public void Valid_signature_over_the_raw_body_is_accepted()
    {
        var verifier = new GitHubHmacSignatureVerifier(new EnvGitIngestionSecretsResolver());
        var body = Encoding.UTF8.GetBytes("{\"ref\":\"refs/heads/main\"}");

        verifier.Verify(body, Sign(Secret, body)).Should().BeTrue();
    }

    [Fact]
    public void Tampered_body_is_rejected()
    {
        var verifier = new GitHubHmacSignatureVerifier(new EnvGitIngestionSecretsResolver());
        var body = Encoding.UTF8.GetBytes("{\"ref\":\"refs/heads/main\"}");
        var signature = Sign(Secret, body);
        var tamperedBody = Encoding.UTF8.GetBytes("{\"ref\":\"refs/heads/evil\"}");

        verifier.Verify(tamperedBody, signature).Should().BeFalse();
    }

    [Fact]
    public void Wrong_secret_is_rejected()
    {
        var verifier = new GitHubHmacSignatureVerifier(new EnvGitIngestionSecretsResolver());
        var body = Encoding.UTF8.GetBytes("{\"ref\":\"refs/heads/main\"}");

        verifier.Verify(body, Sign("a-different-secret", body)).Should().BeFalse();
    }

    [Fact]
    public void Missing_header_is_rejected()
    {
        var verifier = new GitHubHmacSignatureVerifier(new EnvGitIngestionSecretsResolver());
        var body = Encoding.UTF8.GetBytes("{}");

        verifier.Verify(body, null).Should().BeFalse();
        verifier.Verify(body, string.Empty).Should().BeFalse();
    }

    [Fact]
    public void Header_missing_the_sha256_prefix_is_rejected()
    {
        var verifier = new GitHubHmacSignatureVerifier(new EnvGitIngestionSecretsResolver());
        var body = Encoding.UTF8.GetBytes("{}");
        var rawHex = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body));

        verifier.Verify(body, rawHex).Should().BeFalse();
    }

    [Fact]
    public void Unconfigured_secret_fails_closed_in_production()
    {
        Environment.SetEnvironmentVariable(EnvGitIngestionSecretsResolver.SecretEnvVar, null);
        var previousAspNetEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
        try
        {
            var verifier = new GitHubHmacSignatureVerifier(new EnvGitIngestionSecretsResolver());
            var body = Encoding.UTF8.GetBytes("{}");

            verifier.Verify(body, Sign("anything", body)).Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousAspNetEnv);
        }
    }

    [Fact]
    public void Unconfigured_secret_falls_back_to_a_fixed_development_secret_outside_production()
    {
        Environment.SetEnvironmentVariable(EnvGitIngestionSecretsResolver.SecretEnvVar, null);
        var previousAspNetEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        try
        {
            var resolver = new EnvGitIngestionSecretsResolver();
            resolver.TryResolveWebhookSecret(out var secret).Should().BeTrue();

            var verifier = new GitHubHmacSignatureVerifier(resolver);
            var body = Encoding.UTF8.GetBytes("{}");

            verifier.Verify(body, Sign(secret, body)).Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousAspNetEnv);
        }
    }
}
