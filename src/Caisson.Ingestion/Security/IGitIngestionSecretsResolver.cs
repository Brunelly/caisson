namespace Caisson.Ingestion.Security;

/// <summary>
/// Resolves the Git webhook HMAC secret (story #62, AC5). Kept behind an interface — never bound into
/// an <c>IOptions</c> POCO, so it can never accidentally be serialized/logged alongside
/// <c>GitIngestionOptions</c> — specifically so the default env-var-backed implementation
/// (<see cref="EnvGitIngestionSecretsResolver"/>) can be swapped for a real Azure Key Vault-backed one
/// later (ADR 0026's documented AC5 gap) without any call site changing.
/// </summary>
public interface IGitIngestionSecretsResolver
{
    /// <summary>
    /// Resolves the currently-configured webhook secret. Throws <see cref="InvalidOperationException"/>
    /// if no secret is configured and the resolver is fail-closed in the current environment (see
    /// <see cref="EnvGitIngestionSecretsResolver"/>).
    /// </summary>
    string ResolveWebhookSecret();

    /// <summary>Whether a webhook secret can currently be resolved, without throwing.</summary>
    bool TryResolveWebhookSecret(out string secret);
}
