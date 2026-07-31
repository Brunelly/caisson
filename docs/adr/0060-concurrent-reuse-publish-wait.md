# 0060 — Concurrent reuse waits for the winner's publish (AC2/NFR3)

## Status

Accepted

## Context

The idempotency store (ADR 0057) inserts an Open `GitPullRequestLink` *reservation* before any GitHub write,
then the winner fills in the real PR number/URL/commit with `MarkPublished(...)`. Under the NFR3 scenario (N=5
concurrent identical requests → 1 PR + 4 reuses), the four losing requests re-read the winner's reservation
*while the winner is still inside its multi-second GitHub publish window* (GET repo → POST refs → PUT contents
→ POST pulls, ~2.4s in e2e). At that moment the row's `PullRequestNumber`/`PullRequestUrl`/`CommitSha` are
still null, so a loser that returned immediately would emit `reused=true` with a **null PR URL/number** —
self-healing on retry, but a literal violation of AC2 ("reuse returns the existing PR URL and metadata"). The
original 5-concurrent test only asserted the `reused` flag counts, so this was uncovered. Forces:

- Reuse must not fire a Key Vault or GitHub call (the AC2 latency path and the "reuse makes zero GitHub
  traffic" invariant).
- The wait must not depend on the injected `TimeProvider`, which is pinned to a fixed instant by the
  deterministic guardrail-branch tests and the e2e harness.
- A winner that never publishes (crash, or a failure that closes the reservation) must not hang a loser
  indefinitely.

## Decision

A reuse resolves through `CompleteReuseAsync` → `AwaitPublishedReuseAsync`, which **polls the idempotency store
only** (DB reads, no Key Vault/GitHub) until the reused link carries published PR metadata, its Open
reservation disappears (winner failed → the caller should retry into a fresh PR), or a bounded wait elapses.
The wait uses a real monotonic `Stopwatch` (not the injected, possibly-pinned `TimeProvider`), bounded by the
new non-secret options `Git:GitHub:ReusePublishWaitMs` (default 10s, above the create budget) and
`ReusePublishPollMs` (default 100ms). On success the loser returns `reused=true` with the winner's full
metadata; if the wait elapses while still unpublished, it returns a distinct **`pr-pending`** status
(`reused=false`, no PR URL) so the caller retries, rather than a metadata-less reuse.

## Consequences

- A concurrent loser's latency now tracks the winner's publish time (bounded by the <8s create budget); the
  sequential fast-reuse path is unaffected (the link is already published, so the poll returns immediately and
  stays within the <3s reuse budget).
- A new response status `pr-pending` joins `pr-created`/`pr-reused`; it maps to a 202 with no error code. UI
  consumers may treat it as "retry shortly".
- Covered by a slow-publish integration test (`OpenDelay` on the fake GitHub client) asserting all four reuses
  carry the winner's full metadata and none is `pr-pending`.
