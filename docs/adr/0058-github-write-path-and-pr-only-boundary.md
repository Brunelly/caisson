# 0058 — GitHub write path (thin typed REST client) and the structural no-merge boundary

## Status

Accepted

## Context

Story #172 must write to GitHub — create a feature branch, commit the desired-state file, and open a PR —
while guaranteeing the API can never push to, merge into, or otherwise mutate the default branch (safety
boundary; NFR4). The existing ingestion Git path is strictly read-only (`Caisson.Ingestion.Git.ReadOnly`,
guarded by a reflection test). Forces:

- The write client must not blur the read-only boundary or trip its guard.
- A dependency that exposes merge/force-push (e.g. the full Octokit surface) makes "we can't merge" a review
  claim rather than a structural fact.
- GitHub error responses and the bearer token must never leak into logs.
- The default branch used as the PR base and guardrail authority must be trustworthy regardless of GitHub
  branch-protection configuration.

## Decision

1. **A capability-limited interface, `IGitHubPullRequestClient`, in a new `Caisson.Ingestion.Git.GitHub`
   namespace** (distinct from `Git.ReadOnly`, so the read-only guard stays scoped). It exposes ONLY: read repo
   metadata/default branch, read a branch head, read file metadata, create a NEW feature ref, commit the file
   onto a feature branch, and open/find a PR. It has NO `Merge*`, `Force*`, `Push*`, `DeleteBranch*`,
   `UpdateDefault*`, or generic `UpdateReference` method — the PR-only guardrail is **structural**. A
   reflection guard test (`GitHubWriteBoundaryGuardTests`, the inverse of the read-only guard) fails the build
   if a forbidden verb is ever added (NFR4).
2. **A thin typed `HttpClient` implementation, `GitHubRestPullRequestClient`, over the BCL client — not
   Octokit.** Modelled on `RedfishClient`: System.Text.Json, GitHub API-version + User-Agent headers, bearer
   token from `IGitCredentialProvider` set on the request only, cancellation, a bounded per-request timeout,
   and a light retry on 429/5xx. No Octokit (or any new HTTP) dependency is added; `AddHttpClient` comes from
   the ASP.NET Core shared framework at the DI site.
3. **Redaction:** the Authorization header and all response bodies are never logged. Failures log
   method + path + status only and surface as a typed `GitHubApiException(statusCode)`; the publisher maps it
   to the stable `GITHUB_API_FAILED` code.
4. **Default branch is read from repository metadata** (`GetRepositoryAsync`) and is authoritative over the
   configured `DefaultBranch`; it is used both as the PR base and as the `PrOnlyGuardrail` comparison target.
5. **Explicit defense-in-depth guardrail, `PrOnlyGuardrail.EnsureNotDefaultBranch`,** throws
   `PrOnlyGuardrailViolationException` before any branch-create/commit/PR-open if the feature branch is empty
   or equals the (metadata-derived) default branch — even though the interface already makes a default-branch
   write structurally impossible.

## Consequences

- "This API cannot merge or push to the default branch" is a compile-time/type-level fact provable by a test,
  not a code-review assertion.
- Adding Octokit later (e.g. for richer PR features) would require re-justifying the boundary; the thin client
  keeps the surface auditable.
- GitHub Enterprise and the e2e fake server are supported by overriding `ApiBaseUrl`.
