# GitHub PR publishing for rack desired-state changes

Story #172 adds an API that turns a gate-passed rack desired-state candidate into a **GitHub pull request**,
idempotently, with a hard **PR-only guardrail** and credentials retrieved from **Azure Key Vault via managed
identity**. This enables SOC2-aligned change control that holds even if GitHub branch-protection settings are
misconfigured.

> **Safety guarantee.** This API can only create a feature branch, commit the desired-state file onto it, and
> open/read a pull request. It **cannot** merge, force-push, push to the default branch, delete a branch, or
> update the default ref. That boundary is structural — the `IGitHubPullRequestClient` interface exposes no
> such operation and a reflection guard test fails the build if one is ever added (ADR 0058) — and is
> re-checked at runtime by `PrOnlyGuardrail` against the branch the GitHub API itself reports as the default.

## Endpoint

`POST /api/racks/{rackId}/desired-state/prs` — the existing story-#170 desired-state PR endpoint (reused
rather than adding a parallel `/git/prs` controller; see ADR 0056). It requires the
`NetworkConfigAuthor` permission (ReadOnly → 403, anonymous → 401), rate-limits, and re-validates the
candidate server-side before any git write. On success it returns `202 Accepted` with the PR url, number,
branch, commit SHA, candidate fingerprint, `reused` flag, repo owner/name, and a structured change summary.

## Configuration (`Git:GitHub`)

All keys are **non-secret** and live in `appsettings.json` / environment variables. The GitHub token is
**never** configured here — it is read at runtime from Key Vault.

| Key | Meaning | Example |
| --- | --- | --- |
| `Git:GitHub:Enabled` | Turn the real publisher on (else the #170 stub ships) | `true` |
| `Git:GitHub:RepoOwner` | Target repository owner (org or user) | `brunelly` |
| `Git:GitHub:RepoName` | Target repository name | `rack-desired-state` |
| `Git:GitHub:DefaultBranch` | Configured default branch (advisory; GitHub metadata is authoritative) | `main` |
| `Git:GitHub:ApiBaseUrl` | GitHub REST base URL (override for GitHub Enterprise) | `https://api.github.com` |
| `Git:GitHub:AuthMode` | `Pat` (v1) or `GitHubApp` (future) | `Pat` |
| `Git:GitHub:KeyVaultUri` | Key Vault URI the PAT is read from | `https://myvault.vault.azure.net/` |
| `Git:GitHub:PatSecretName` | Name (not value) of the Key Vault secret holding the PAT | `github-pat` |
| `Git:GitHub:BranchPrefix` | Feature-branch prefix | `caisson` |
| `Git:GitHub:CommitPathTemplate` | Committed file path; `{slug}` = rack external key | `desired-state/racks/{slug}.yaml` |

The committed path **must match the ingestion read path** so a created PR feeds back through ingestion
unchanged (the default matches `GitIngestion:PathGlob`, `desired-state/racks/*.yaml`).

### Environment-variable names (hosted)

ASP.NET Core binds nested config from `__`-delimited env vars, e.g.
`Git__GitHub__Enabled`, `Git__GitHub__RepoOwner`, `Git__GitHub__KeyVaultUri`, `Git__GitHub__PatSecretName`.
Local/CI runs that use the env credential provider read the token from `CAISSON_GITHUB_TOKEN`.

## Credentials: Key Vault + managed identity (AC4)

The PAT is retrieved at runtime by `KeyVaultGitCredentialProvider` using `DefaultAzureCredential`
(managed identity) with a short (5-minute) in-memory cache. No secret is ever written to `appsettings.json`,
source, the options POCO, or logs (ADR 0059). Setup:

1. **Enable the Container App's system-assigned managed identity.**
   `az containerapp identity assign -g <rg> -n <app> --system-assigned`
2. **Grant it least-scope Key Vault Secrets User** on the target vault.
   `az role assignment create --assignee-object-id <principalId> --assignee-principal-type ServicePrincipal
   --role "Key Vault Secrets User" --scope $(az keyvault show -n <vault> --query id -o tsv)`
3. **Store the PAT** in Key Vault under the name you set in `PatSecretName`.
   `az keyvault secret set --vault-name <vault> --name github-pat --value <PAT>`
4. **Pass only the non-secret references** (`Git__GitHub__KeyVaultUri`, `Git__GitHub__PatSecretName`) to the
   app. The deploy workflow does this from repository variables — the PAT itself is never copied into an
   Actions secret or a Container App secret.

The deployment **fails closed**: `GitPrStartupGuard` refuses to boot in Production when the feature is enabled
without a resolvable repo and Key Vault URI + secret name (no static/env PAT fallback is permitted in hosted
environments).

### PAT scope, naming and rotation

- **Required repo permissions:** `contents:write` (create branch + commit the file) and `pull_requests:write`
  (open the PR) on the single target repository. No admin, no `workflow`, no org scope.
- **Naming:** use a stable secret name (e.g. `github-pat`) and reference it via `PatSecretName`.
- **Rotation:** store the new PAT as a new version of the same Key Vault secret. The provider's short cache
  picks up the rotation within ~5 minutes (or immediately after an auth failure clears the cache) — no redeploy
  is required.

## Branch, title and body formats (AC1)

- **Branch:** `caisson/{rackSlug}/op-{operatorSlug}/{yyyyMMddTHHmmssZ}-{fingerprint12}`
  (e.g. `caisson/rack-a/op-jdoe/20260730T153045Z-1a2b3c4d5e6f`). The short fingerprint suffix prevents
  same-second collisions between distinct candidates.
- **Title:** `Rack {rackSlug}: network desired-state update ({operator})`.
- **Body:** a human-readable change summary plus a fenced machine-readable ```json``` block carrying the rack,
  operator, timestamp, candidate fingerprint, validation-run id, acknowledged warnings, correlation id, and
  structured change counts.

## Idempotency (AC2, NFR3)

The candidate **fingerprint** is the SHA-256 of its canonical YAML (order-independent). An open
`git_pull_request_link` row per `(rack, fingerprint)` is enforced by a filtered partial-unique index, so a
repeat request reuses the existing open PR (`reused=true`, no Key Vault/GitHub call) and N concurrent identical
requests collapse to exactly one PR. A closed/merged PR for the same candidate does not block a fresh one.

A concurrent **loser** re-reads the winner's reservation, which may still be unpublished during the winner's
multi-second GitHub publish window. To honour AC2 ("reuse returns the existing PR URL and metadata"), a reuse
waits (DB polling only — no Key Vault/GitHub call, bounded by `Git:GitHub:ReusePublishWaitMs`, default 10s) for
the winner to populate the PR number/URL/commit before returning `reused=true` with the full metadata. If the
winner is still publishing when the wait elapses (or its reservation was closed by a failure), the response is
a distinct `pr-pending` status (`reused=false`, no PR URL) so the caller retries — never `reused=true` with a
null URL.

## Error codes (AC6)

| Code | Meaning | HTTP |
| --- | --- | --- |
| `PR_ONLY_GUARDRAIL_VIOLATION` | The request would write to the default branch | 409 |
| `GIT_CREDENTIALS_UNAVAILABLE` | The PAT could not be retrieved from Key Vault | 502 |
| `GIT_REPO_NOT_CONFIGURED` | No target repository is configured | 500 |
| `GITHUB_API_FAILED` | A GitHub API call failed (no PR created) | 502 |
| `UNEXPECTED_ERROR` | An unexpected error aborted PR creation | 500 |

Every create/reuse/refuse/fail is audited (`git.pr.created` / `git.pr.reused` / `git.pr.refused_pr_only` /
`git.pr.failed`) with the correlation id, rack, fingerprint, repo, branch and PR metadata — never the PAT or
the candidate YAML.

## PR status polling, checks, gating and events (story #173)

Once a PR exists, a background poller (`GitPullRequestStatusPoller`) periodically reads its GitHub state and
check-runs, persists the result to the separate 1:1 `git_pull_request_status` table (ADR 0061), and — only on a
*meaningful* transition (a change in PR state or the rolled-up checks conclusion) — writes an append-only
`topology_audit_event` in the SAME transaction as the status upsert and publishes a `git-pr-status-changed`
event over the existing Redis pub/sub → SignalR pipeline to the rack group. No-op polls and transient failures
produce neither an audit row nor an event.

Concurrency across API replicas uses the codebase's `UPDATE ... FOR UPDATE SKIP LOCKED ... RETURNING id`
lease (ADR 0063), so two replicas never double-claim a PR and the ≤2-GitHub-calls-per-PR-per-cycle budget
(NFR1) holds. Each claimed PR makes exactly two GitHub reads: the PR (`GET repos/{o}/{r}/pulls/{n}`) then the
check-runs for the PR's head SHA (`GET repos/{o}/{r}/commits/{sha}/check-runs?per_page=100`).

Apply/promote is **blocked until the exact candidate's PR is merged** (ADR 0062): `DriftApplyController`
returns RFC 7807 `409 Conflict` with `reasonCode` `PrNotMerged`/`NoPrLinked` before any job is created, and
`DriftApplyJobService` re-checks as defence-in-depth. The rack-scoped read APIs
`GET api/racks/{rackId}/git/pull-request` and `.../pull-request/events` (RBAC `TopologyRead`, rack-access
checked first) drive the UI panel and its SignalR-down fallback polling.

### Configuration (`GitPullRequestStatus` section, non-secret)

Credentials still come ONLY from Key Vault / managed identity via `IGitCredentialProvider` — never from this
section, source, or logs. The read client reuses the `Git:GitHub` `ApiBaseUrl`/`RepoOwner`/`RepoName`.

| Key | Meaning | Default | Bounds |
| --- | --- | --- | --- |
| `Enabled` | Whether the poller runs | `false` | — |
| `PollIntervalSeconds` | Poll cadence (NFR1) | `300` (prod); `60` in Development/CI | 60–600 |
| `BatchSize` | Max due PRs claimed + polled per tick | `20` | 1–500 |
| `MaxBackoffSeconds` | Cap on exponential backoff-with-jitter after a failed poll | `600` | 1–86400 |
| `LeaseSeconds` | Lease horizon a claim advances `NextPollAfterUtc` to (crash recovery) | `120` | 30–3600 |
| `DegradedAfterMinutes` | Health `Degraded` threshold when GitHub is unreachable (NFR3) | `15` | 1–1440 |

Per-environment interval defaults: **dev/CI 60s**, **prod 300s** (`appsettings.json` = 300; `appsettings.Development.json` overrides to 60).

### Failure handling

- `401`/`403` → a failed poll recorded with the sanitized reason `CredentialsRejected` (no audit, no event) and
  exponential backoff-with-jitter into `NextPollAfterUtc`.
- `429` → honours `Retry-After` / `X-RateLimit-Reset` into `NextPollAfterUtc`; the last-known status stays
  visible in the UI with its `Last updated` timestamp.
- timeout / `5xx` / transport → exponential backoff-with-jitter. Every fault is isolated per-PR so one poisoned
  PR never aborts the batch or crashes the host (NFR3).

### Health & metrics

`GitPullRequestStatusHealthCheck` (`/health/ready`, tag `git-pr-status`) reports `Degraded` — never
`Unhealthy`/throwing, and without any live GitHub call — when no successful poll is newer than
`DegradedAfterMinutes` while polls are actively failing. `GitPullRequestStatusMetrics`
(meter `Caisson.Ingestion.GitPrStatus`) emits poll attempts/results/duration, rows claimed, transitions,
poll-failures-by-reason, GitHub call count, and the last-successful-GitHub-contact gauge.

### Status transition audit actions

`git.pr.status_changed` and `git.pr.checks_changed` (target type `git-pull-request`, actor `system`) — each with
rackId, prNumber, repo, previous/new state + checks, headSha and the tick correlation id, secret-scrubbed and
bounded to 8 KB.
