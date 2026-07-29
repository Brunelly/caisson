using Caisson.Ingestion.Security;

namespace Caisson.Api.IntegrationTests;

/// <summary>
/// A fixed-secret <see cref="IGitIngestionSecretsResolver"/> for the API test host — avoids mutating the
/// process-wide <c>CAISSON_GIT_WEBHOOK_SECRET</c> environment variable, which other test classes in this
/// assembly (e.g. <c>GitIngestionStartupGuardTests</c>) also touch and which xUnit may run concurrently
/// with this shared, collection-level factory.
/// </summary>
public sealed class FixedGitIngestionSecretsResolver : IGitIngestionSecretsResolver
{
    /// <summary>The known secret this test host's webhook signatures are computed against.</summary>
    public const string Secret = "api-integration-test-webhook-secret";

    public string ResolveWebhookSecret() => Secret;

    public bool TryResolveWebhookSecret(out string secret)
    {
        secret = Secret;
        return true;
    }
}
