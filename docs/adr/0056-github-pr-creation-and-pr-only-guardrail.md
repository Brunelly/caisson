# 0056 — GitHub PR creation API contract, branch naming, and fingerprint conventions

## Status

Accepted

## Context

Story #172 turns a gate-passed rack desired-state candidate into a GitHub pull request, idempotently, with
a hard PR-only guardrail and Key Vault credentials. It builds directly on story #170, which shipped the
`DesiredStatePrController` (`POST api/racks/{rackId:guid}/desired-state/prs`) and the `IDesiredStatePrService`
seam with a `NotYetEnabledDesiredStatePrService` stub. Forces:

- The story text names a new `POST /api/racks/{rackIdOrSlug}/git/prs` endpoint, but #170 already built a
  purpose-made PR seam with RBAC (`NetworkConfigAuthor`), rate limiting, server-side re-validation,
  correlation, and audit helpers. There is no id-or-slug route resolver anywhere in the codebase.
- The response DTO (`CreatePrResponse`) is already serialized and consumed by the web client; new fields must
  not break it.
- The idempotency fingerprint must be stable across semantically-irrelevant differences (collection ordering).
- The branch name must be deterministic, traceable, git-ref-safe, and can never equal the default branch.

## Decision

1. **Fill the existing `DesiredStatePrController`/`IDesiredStatePrService` seam rather than add a parallel
   `/git/prs` controller.** This reuses #170's shipped RBAC/rate-limit/re-validation/audit machinery, avoids a
   duplicate PR path (an audit hazard), needs no id-or-slug resolver, and yields a stronger guardrail (a
   candidate that failed preflight can never be turned into a PR). Every AC is still met; the endpoint route is
   `POST api/racks/{rackId:guid}/desired-state/prs`.
2. **`CreatePrResponse` and `DesiredStatePrCreationResult` are extended additively.** All new fields
   (`PullRequestNumber`, `BranchName`, `CommitSha`, `CandidateFingerprint`, `Reused`, `RepoOwner`, `RepoName`,
   `ErrorCode`, and a structured `ChangeSummary`) are nullable/defaulted, so existing 4-argument construction,
   serialization, and the web client stay unbroken.
3. **Fingerprint = SHA-256 of canonical YAML** (story Q1). `CandidateFingerprint.Compute(model)` renders the
   candidate through the same `DesiredStateYamlRenderer` the ingestion read path uses and hashes it with the
   shared `DesiredStateContentHash` (lowercase 64-hex). Because the renderer canonicalizes, reordered VLAN/port
   collections produce the identical fingerprint. It lives in `Caisson.Ingestion` (not `Caisson.Domain`, as the
   task file list suggested) because it must call the renderer — `Caisson.Domain` carries no ingestion
   dependency by layering rule (ADR 0001).
4. **Branch naming** is `caisson/{rackSlug}/op-{operatorSlug}/{yyyyMMddTHHmmssZ}-{fingerprint12}` via the pure
   `PrBranchNaming` helper: `rackSlug` is the rack's `ExternalKey`, `operatorSlug` is the slugified `oid`/UPN
   claim, and the short 12-hex fingerprint suffix prevents same-second collisions between distinct candidates
   while preserving the story's readable convention. Slugification is lowercase ASCII-alphanumeric-plus-single-
   hyphen with truncation and a non-empty fallback, so invalid/Unicode/over-long identifiers can never form a
   ref GitHub rejects, and the multi-segment `caisson/` prefix plus timestamp means the result can never equal a
   bare default branch.
5. **Stable error codes** live in `GitPrErrorCodes` (UPPER_SNAKE + `MessageFor`, mirroring
   `DriftApplyErrorCodes`); **durable audit actions** live in `GitPrAuditActions` (lowercase-dotted
   `git.pr.created`/`reused`/`refused_pr_only`/`failed`, mapped to the story's `GIT_PR_*` event types).
6. **Non-secret options** live in `GitHubOptions` (section `Git:GitHub`) with no secret field — the PAT resolves
   at runtime through `IGitCredentialProvider` (ADR 0059), never through the options POCO.

## Consequences

- The story's literal `/git/prs` route is not added; API consumers use the existing desired-state PR endpoint.
  This is a deliberate divergence flagged here and satisfies every AC.
- `GitPullRequestLink` is a new mutable domain entity (ADR 0057) modelled on `DesiredStateCandidateDiffCache`.
- The fingerprint helper's placement in `Caisson.Ingestion` means its unit tests live in
  `Caisson.Ingestion.Tests`, not `Caisson.Domain.Tests`.
