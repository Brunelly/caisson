namespace Caisson.Ingestion.Security;

/// <summary>
/// Default <see cref="IGitIngestionSecretsResolver"/>: reads the webhook secret from the
/// <c>CAISSON_GIT_WEBHOOK_SECRET</c> environment variable. Deliberately extends the codebase's existing,
/// three-times-established env-var + fail-closed-in-Production convention
/// (<c>TopologyEventAuthenticity.ResolveKey</c>, <c>CursorCodec.ResolveKey</c>,
/// <c>RedisEventAuthenticityStartupGuard</c>) rather than introducing the Azure Key Vault SDK as a
/// first-of-its-kind dependency for one new secret (ADR 0026's documented AC5 gap).
/// </summary>
public sealed class EnvGitIngestionSecretsResolver : IGitIngestionSecretsResolver
{
    /// <summary>The environment variable the webhook secret is read from.</summary>
    public const string SecretEnvVar = "CAISSON_GIT_WEBHOOK_SECRET";

    private const string DevelopmentSecret =
        "insecure-development-only-git-webhook-secret-do-not-use-in-production";

    public string ResolveWebhookSecret()
    {
        if (TryResolveWebhookSecret(out var secret))
        {
            return secret;
        }

        throw new InvalidOperationException(
            $"{SecretEnvVar} must be configured under ASPNETCORE_ENVIRONMENT=Production — refusing to " +
            "fall back to the fixed development secret.");
    }

    public bool TryResolveWebhookSecret(out string secret)
    {
        var configured = Environment.GetEnvironmentVariable(SecretEnvVar);
        if (!string.IsNullOrEmpty(configured))
        {
            secret = configured;
            return true;
        }

        if (IsProductionEnvironment())
        {
            secret = string.Empty;
            return false;
        }

        secret = DevelopmentSecret;
        return true;
    }

    private static bool IsProductionEnvironment()
    {
        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        return string.Equals(env, "Production", StringComparison.OrdinalIgnoreCase);
    }
}
