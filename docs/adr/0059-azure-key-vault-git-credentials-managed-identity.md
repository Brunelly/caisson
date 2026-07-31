# 0059 — Azure Key Vault git credentials via managed identity (PAT-first, App-later)

## Status

Accepted

## Context

Story #172 requires that the GitHub credential granting write access to the source-of-truth repository is
never stored in appsettings, source, or logs, and is retrieved at runtime via managed identity (AC4; NFR2).
The codebase had, until now, no Azure SDK dependency — the webhook-secret path deliberately used an env-var
resolver (ADR 0026) to avoid being the first to add one. A write credential is materially more sensitive than
the webhook HMAC secret, so this story is where the Key Vault dependency is justified. Forces:

- The token must be fetchable per request without a Key Vault round-trip every time (latency budgets: ≤3s
  reuse, ≤8s create) yet must pick up rotation reasonably quickly.
- Tests and local/CI runs must never touch Azure.
- The v1 credential is a PAT, but the contract must not preclude a future GitHub App installation token
  (story Q3).
- License compliance: the project is Apache-2.0 (NFR5).

## Decision

1. **`IGitCredentialProvider` seam** returning a redacting `GitHubCredential` (masked `ToString`, value only
   via an explicit `Reveal()` used at the moment the Authorization header is set). The interface is
   PAT-first but stable, so a `GitHubAppCredentialProvider` can be added later with no call-site change.
2. **`KeyVaultGitCredentialProvider`** (hosted default) uses a lazily-created singleton
   `SecretClient(new Uri(KeyVaultUri), new DefaultAzureCredential())`, fetches the secret by NAME, and caches
   the value in `IMemoryCache` with a 5-minute TTL (balancing rotation latency vs Key Vault throttling). Any
   auth/fetch failure clears the cache and throws `GitCredentialUnavailableException` with no secret text; the
   publisher surfaces `GIT_CREDENTIALS_UNAVAILABLE`. The single Key-Vault-touching call is an overridable
   `FetchSecretAsync` seam so caching/redaction can be tested without Azure.
3. **`EnvGitCredentialProvider`** (local/CI/test default) reads `CAISSON_GITHUB_TOKEN`. Unlike the webhook
   secret, there is NO fixed development fallback for a write credential; an unset variable always fails
   closed, and the message notes hosted environments must use Key Vault.
4. **`GitPrStartupGuard`** fails the boot when the feature is enabled without a target repo, and — in
   Production — without a resolvable Key Vault URI + secret name (PAT mode). No static/env PAT fallback is
   permitted in hosted environments.
5. **Dependencies:** `Azure.Identity` 1.12.1 and `Azure.Security.KeyVault.Secrets` 4.6.0, pinned centrally in
   `Directory.Packages.props`. Both are MIT-licensed — compatible with Apache-2.0 (NFR5) — and are the first
   Azure SDK dependencies in the tree.

## Consequences

- The PAT lives only in Key Vault; it is never in appsettings, the options POCO, source, or logs. The DI layer
  binds only `KeyVaultUri` and the secret NAME.
- The 5-minute cache means a rotated secret is honoured within at most five minutes (or immediately after an
  auth failure clears the cache), an acceptable trade-off for the latency budgets.
- A CI OSS-license gate must cover the two new Azure packages (added in the CI step, story task #209).
- Managed identity must be granted "Key Vault Secrets User" on the target vault (documented in
  `docs/github-pr-publishing.md`).
