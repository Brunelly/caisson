namespace Caisson.Api.Options;

/// <summary>
/// The GitHub credential-provider authentication mode (story #172, Q3). PAT-first for v1; the interface and
/// options carry <see cref="GitHubApp"/> so a future GitHub App installation-token provider can be selected
/// without a contract change.
/// </summary>
public enum GitPrAuthMode
{
    /// <summary>A GitHub Personal Access Token retrieved from Key Vault.</summary>
    Pat,

    /// <summary>A GitHub App installation token minted from an App private key (future; story Q3).</summary>
    GitHubApp,
}

/// <summary>
/// Non-secret configuration for the desired-state GitHub PR-creation feature (story #172), config-bound under
/// <see cref="SectionName"/> (<c>Git:GitHub</c>). Mirrors <c>GitIngestionOptions</c>: it carries NO
/// secret-shaped field — the PAT (or App key) resolves at runtime through <c>IGitCredentialProvider</c>
/// (Key Vault via managed identity), never through this POCO, so a credential can never be serialized/logged
/// alongside these settings or committed to <c>appsettings.json</c> (AC4, NFR2). Only <see cref="PatSecretName"/>
/// (the Key Vault secret <em>name</em>, not its value) and <see cref="KeyVaultUri"/> are bound here.
/// </summary>
public sealed class GitHubOptions
{
    /// <summary>Configuration section name (<c>Git:GitHub</c>).</summary>
    public const string SectionName = "Git:GitHub";

    /// <summary>Whether the real GitHub PR publisher is active (else the stub publisher ships).</summary>
    public bool Enabled { get; set; }

    /// <summary>The target repository owner (org or user).</summary>
    public string RepoOwner { get; set; } = string.Empty;

    /// <summary>The target repository name.</summary>
    public string RepoName { get; set; } = string.Empty;

    /// <summary>
    /// The configured default branch. Advisory only: the guardrail authority is the branch reported by the
    /// GitHub repository metadata, which is read at request time and takes precedence over this value.
    /// </summary>
    public string DefaultBranch { get; set; } = "main";

    /// <summary>The GitHub REST API base URL (overridable for GitHub Enterprise and for e2e fakes).</summary>
    public string ApiBaseUrl { get; set; } = "https://api.github.com";

    /// <summary>The credential authentication mode (PAT-first; App later).</summary>
    public GitPrAuthMode AuthMode { get; set; } = GitPrAuthMode.Pat;

    /// <summary>The Azure Key Vault URI the credential is fetched from (e.g. <c>https://myvault.vault.azure.net/</c>).</summary>
    public string? KeyVaultUri { get; set; }

    /// <summary>The name (not value) of the Key Vault secret holding the GitHub PAT.</summary>
    public string? PatSecretName { get; set; }

    /// <summary>The branch-name prefix for generated feature branches (matches <c>PrBranchNaming.DefaultPrefix</c>).</summary>
    public string BranchPrefix { get; set; } = "caisson";

    /// <summary>
    /// The committed desired-state file path template; <c>{slug}</c> is replaced by the rack's external key.
    /// Must match the ingestion read path so a created PR feeds back through ingestion unchanged.
    /// </summary>
    public string CommitPathTemplate { get; set; } = "desired-state/racks/{slug}.yaml";
}
