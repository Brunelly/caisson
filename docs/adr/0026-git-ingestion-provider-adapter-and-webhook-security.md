# 0026 — Git ingestion provider adapter and webhook security

## Status

Accepted

## Context

Story #62 must fetch desired-state YAML from a single configured Git repository/branch, on a poll
interval and via a provider webhook, and must do so idempotently, safely under concurrent triggers
(NFR3), and without ever logging or exposing a credential/webhook secret (AC5). It must also fit the
existing layering rule (ADR 0001/0013: one layer owns each new external dependency; `Caisson.Domain`
stays dependency-free) and the existing three-times-established env-var secret convention
(`TopologyEventAuthenticity`, `CursorCodec`, `RedisEventAuthenticityStartupGuard`) rather than
introduce a new secrets mechanism as a first-of-its-kind dependency.

## Decision

- **New `Caisson.Ingestion` project.** The git adapter, YAML parser/validator/materialiser and webhook
  HMAC verifier (the parsing "core") reference only `Caisson.Domain`, mirroring `Caisson.Correlation`'s
  pure layering: no EF Core, no reference to any `Caisson.Drivers.*` assembly. This keeps the M0
  driver-boundary guard tests intact and follows ADR 0013's rule that a new external dependency (Git,
  YAML) gets its own owning layer rather than leaking into `Caisson.Domain`. The orchestration layer
  added on top in the same project (`DesiredStateIngestionService`, the poll scheduler, the webhook
  drainer) additionally references `Caisson.Infrastructure` for `CaissonDbContext` — this is the exact
  same shape `Caisson.Orchestration` already has for discovery (Domain + Infrastructure, no driver
  assembly named), not a new pattern. `Caisson.Ingestion` still never references any `Caisson.Drivers.*`
  assembly, which is what the M0 guard tests actually check.
- **LibGit2Sharp over shelling out to the `git` CLI.** A library call has zero command-injection surface
  — consistent with the hardening batch (story #104) that removed shell-invocation patterns elsewhere.
  `LibGit2Sharp` is dual-licensed MIT (wrapper) / GPLv2-with-linking-exception (native `libgit2`), which
  is compatible with distributing a proprietary application; noted here since it's the first native/
  interop dependency in the solution.
- **Provider-interface, not a provider registry.** `IGitRepositoryProvider` lives in a `ReadOnly`
  namespace (same safety-boundary pattern as `Caisson.Drivers.Abstractions.ReadOnly`, enforced by a
  reflection guard banning write-verb method names) with a single concrete implementation,
  `LibGit2SharpRepositoryProvider`. The interface exists for future GitHub/GitLab/Azure DevOps
  variation (Technical Constraints), but M1 needs no registry/resolution logic since only one
  repo/branch is configured per installation (Q1's single-repo assumption) — a registry would be
  speculative complexity for a need that doesn't exist yet.
- **Idempotency and replay protection are DB constraints, not application-only checks.** Exactly as
  `DiscoveryJobService`/`ux_discovery_job_rack_active` do for discovery jobs,
  `DesiredStateIngestionService.RunAsync` inserts a `DesiredStateIngestionRun` row and relies on two
  partial-unique indexes — one on `commit_sha` (filtered to live/terminal-success statuses) and one on
  `webhook_delivery_id` (filtered to non-null) — catching the resulting `DbUpdateException`/
  `PostgresException.UniqueViolation` race and returning the existing run as a no-op (NFR2/NFR3). This
  is safe under concurrent poll + webhook triggers for the same commit by construction, not by locking.
- **GitHub `X-Hub-Signature-256` HMAC first (Q1's answer).** `GitHubHmacSignatureVerifier` computes
  `sha256=` + hex(HMACSHA256(secret, rawBody)) and compares with `CryptographicOperations.FixedTimeEquals`
  — the same shape as `TopologyEventAuthenticity`. The raw request body is captured via
  `Request.EnableBuffering()` before any model binding, since the signature is computed over exact
  bytes, not a re-serialized model. `IWebhookSignatureVerifier` is an interface so a GitLab/Azure DevOps
  variant can be added later without touching the controller.
- **Secret resolution extends the existing env-var + fail-closed-in-Production convention.**
  `EnvGitIngestionSecretsResolver` reads `CAISSON_GIT_WEBHOOK_SECRET`, falling back to a fixed,
  documented development value UNLESS `ASPNETCORE_ENVIRONMENT=Production`, in which case an unset
  secret throws at startup (`GitIngestionStartupGuard`, mirroring `JwtAuthorityStartupGuard`) rather than
  silently accepting unsigned webhooks. This deliberately extends a pattern already used three times
  (`TopologyEventAuthenticity.ResolveKey`, `CursorCodec.ResolveKey`, `RedisEventAuthenticityStartupGuard`)
  instead of introducing the Azure Key Vault SDK as a first-of-its-kind dependency for one new secret.
  The secret is resolved through `IGitIngestionSecretsResolver`, never bound into an `IOptions` POCO
  (so it can never accidentally be serialized/logged alongside `GitIngestionOptions`).

## Consequences

- **Gap against AC5/the story's stated Key Vault expectation.** The story's acceptance criteria describe
  Azure Key Vault-backed secret storage. This ADR accepts a gap here: real Key Vault wiring (managed
  identity, `Azure.Security.KeyVault.Secrets`) is deferred to an isolated follow-up story. Because the
  secret is resolved behind `IGitIngestionSecretsResolver`, swapping the env-var-backed default for a
  Key Vault-backed implementation later is a new implementation of one interface, not a call-site change.
  Until then, the secret must be provisioned as `CAISSON_GIT_WEBHOOK_SECRET` alongside the other
  HMAC-key env vars this deployment already requires.
- **Unauthenticated-HTTPS-only Git access for M1** (Q1 assumption). `IGitRepositoryProvider` accepts an
  optional, currently-unused opaque credentials-ref parameter so a later private-repo story is a new
  branch inside the existing method, not a signature change.
- A webhook secret rotation invalidates in-flight (already-sent, unverified) deliveries for the rotation
  window — acceptable, since a missed webhook is recovered by the next poll tick (the poll and webhook
  paths are fully interchangeable through the same `RunAsync` idempotency key).
- No resumable multi-step claim/heartbeat runner (`DiscoveryJobRunner`'s shape) is introduced for
  ingestion: parsing + validating + materialising ~100 rack files / 10k ports is a single bounded
  operation well under NFR4's 30s P95 budget, so a lightweight signal + background drainer (webhook
  returns 202 immediately; processing happens reliably in-process) is sufficient and considerably
  simpler than a resumable runner.
