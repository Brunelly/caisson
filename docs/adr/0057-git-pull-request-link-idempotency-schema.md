# 0057 — GitPullRequestLink idempotency schema and concurrency

## Status

Accepted

## Context

Story #172 requires idempotent PR creation: N concurrent identical requests must yield exactly one PR and
four reuse responses (NFR3), a repeat request for the same candidate must reuse the existing open PR (AC2),
and a Closed/Merged PR for the same candidate must NOT block a fresh one (story Q2 — always a new branch+PR
after the prior closes). The story's data model proposes a `GitPullRequestLink` table for the fingerprint→PR
mapping (Q4 answer: DB as source of truth). Forces:

- The mapping row must be updatable (reservation → published; open → closed/merged), so it cannot be
  append-only.
- The uniqueness constraint must be scoped to open links only.
- Concurrency resolution must not blanket-swallow write failures (which would mask real bugs).

## Decision

1. **`GitPullRequestLink` is a mutable POCO** (private EF ctor, private setters, `Bound(...)` length guards),
   modelled on `DesiredStateCandidateDiffCache`/`RackNetworkIntent` and explicitly NOT `IAppendOnly`: the row
   is inserted as an Open *reservation* before any GitHub write, then `MarkPublished(...)` records the real
   PR number/url/commit SHA, and `UpdateStatus(...)` closes/merges it later.
2. **Filtered partial-unique index** `ux_git_pull_request_link_rack_fingerprint_open` on
   `(rack_id, candidate_fingerprint) WHERE status = 'Open'`, copied from the drift-apply active-job index
   pattern. This DB-enforces one Open link per (rack, candidate) — the whole idempotency/concurrency
   invariant — while a Closed/Merged link with the same fingerprint is invisible to the constraint, so a
   later identical candidate correctly gets a fresh PR.
3. **Insert-then-catch-unique-violation** in `GitPullRequestLinkStore.InsertOrGetExistingAsync`, copied
   precisely from `DriftApplyJobService`: it catches only `PostgresException { SqlState: UniqueViolation }`
   on the *named* constraint, detaches the loser, re-reads the winner, and returns it as a reuse. Any other
   `DbUpdateException` propagates. The fast path `FindOpenByFingerprintAsync` serves the common AC2 reuse with
   no Key Vault or GitHub call.
4. **FK to `rack` is `Restrict`**, `UseXminAsConcurrencyToken()` for optimistic concurrency, enum stored as
   its string name, all lengths bounded — matching every sibling mutable configuration.
5. **Audit** for create/reuse/refuse/fail is emitted through the existing `IAuditEventWriter.WriteActionAsync`
   using the `GitPrAuditActions` strings, with a bounded, `SecretScrubber`-backstopped details JSON carrying
   correlationId, rack id/slug, fingerprint, repo owner/name, branch, prNumber, prUrl, reused, and errorCode
   — never the PAT or candidate YAML.

## Consequences

- The migration `AddGitPullRequestLink` applies, rolls back, and re-applies cleanly against Postgres
  (verified); the snapshot is regenerated and committed with no bin/obj artifacts.
- A reservation that is inserted but never published (a crash between reserve and GitHub create) leaves an
  Open row; a retry re-reads it (the branch/PR state is inspected rather than force-pushed) — reconciliation
  is bounded and never mutates the default branch.
- Because uniqueness is filtered to Open, closing/merging a PR out-of-band frees the fingerprint for a new PR
  without any manual cleanup.
