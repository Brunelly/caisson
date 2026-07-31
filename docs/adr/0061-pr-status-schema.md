# 0061 — PR status projection schema (separate 1:1 table + checks JSONB)

## Status

Accepted

## Context

Story #173 must persist the *current* GitHub state and check-run rollup of each Caisson-created pull request
so the UI can render it, the poller can lease/back-off per PR, and apply can be gated on "merged". Forces:

- `Caisson.Domain.Git.GitPullRequestStatus` already exists as an **enum** (`Open|Closed|Merged`), so the new
  entity cannot reuse that name.
- The record is a mutable projection the poller upserts every cycle — it cannot be append-only.
- Rack-scoped reads (and the SignalR event payload) should need no join back to `git_pull_request_link`.
- The per-check detail is variable-shape and must be bounded and secret-free.

## Decision

1. **New entity `GitPullRequestStatusRecord`** (table `git_pull_request_status`), a distinct type from the
   pre-existing `GitPullRequestStatus` enum, which it **reuses** for its `State`. A new enum
   `GitPullRequestChecksConclusion` (`Success|Failure|Neutral|Cancelled|Skipped|TimedOut|ActionRequired|
   Stale|Pending|Unknown`) models the rolled-up checks conclusion.
2. **Separate 1:1 table**, not columns on `git_pull_request_link`: a unique index on `pull_request_link_id`
   (`ux_git_pull_request_status_link`) with an FK `OnDelete(Restrict)` enforces exactly one status row per
   link. Keeping status out of the idempotency link keeps that hot reservation row small and its filtered
   partial-unique index untouched. Denormalized `rack_id`/`repo_owner`/`repo_name`/`pull_request_number`/
   `pull_request_url` let rack-scoped reads and events avoid a join.
3. **Checks rollup as `jsonb`** (`checks_summary`, bounded ≤16 KB) — a compact per-check list
   (name/status/conclusion/detailsUrl/timings + a truncation indicator) rather than a child `git_pull_request_check`
   table, mirroring the diff/step JSONB precedent (`DriftApplyJobStep`, `RackNetworkIntent`). One row updates
   atomically with the status and there is no per-check fan-out to manage.
4. **Mutable POCO, NOT `IAppendOnly`** (modelled on `GitPullRequestLink`): `UseXminAsConcurrencyToken()`,
   enum-as-string, all lengths bounded. The append-only `DbContext` sweep deliberately does not cover it.
5. **Transition logic in the domain, DB-free:** `ApplyObservation(...)` returns whether a *meaningful*
   (state or checks-conclusion) transition occurred — a head-SHA-only change moves `UpdatedAtUtc` but is not
   meaningful — while `RecordPollSuccess`/`RecordPollFailure` drive the `NextPollAfterUtc` lease/backoff and
   the `ConsecutivePollFailures`/`LastPollFailureReason` counters, preserving last-known status on failure.
6. **Lease index** `ix_git_pull_request_status_lease` on `(next_poll_after_utc, last_checked_at_utc)` backs the
   poller's due-candidate selection; `ix_git_pull_request_status_rack` backs the rack-scoped read.

## Consequences

- The migration `AddGitPullRequestStatus` applies, rolls back, and re-applies cleanly against Postgres
  (verified); the snapshot is regenerated with no bin/obj artifacts.
- The 1:1 unique index means first-sighting must upsert (insert-if-absent) the status row before the poller
  claims it; a link with no status row is simply "not yet polled".
- Because the meaningful-transition decision lives in the domain and is unit-tested DB-free, the poller,
  audit, and event pipeline (Steps 3–4) can gate strictly on it with no risk of no-op audit/event noise.
