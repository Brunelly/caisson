# 0063 — PR status poller: DB lease, backoff, and the ≤2-calls-per-PR budget

## Status

Accepted

## Context

Story #173 must periodically sync each open Caisson-created PR's GitHub state + check-runs across a
multi-replica control plane without throttling GitHub or double-polling. Forces:

- NFR1: rate-limit-aware, ≤2 GitHub requests per PR per poll cycle, interval 60s–10m, honour 429.
- NFR3: resilient to GitHub outages; a hosted background service with per-tick + per-PR exception isolation
  that never crashes the host, and a health endpoint that degrades (never fails) when GitHub is unreachable.
- Story Q3 answer: DB-backed lease for cross-replica concurrency.

## Decision

1. **`GitPullRequestStatusPoller : BackgroundService`** cloned from `GitPollingBackgroundService`
   (`PeriodicTimer` + `TimeProvider`, options-gated `Enabled`, per-tick `try/catch`, an internal `TickAsync`
   for deterministic tests, one correlation id per tick). Each tick opens a DI scope and calls the scoped
   `IGitPullRequestStatusSyncService.SyncDueAsync`.
2. **DB-backed lease via `FOR UPDATE SKIP LOCKED`** (not xmin-CAS), modelled on
   `DiscoveryJobRunner`/`DriftApplyJobRunner`: `UPDATE git_pull_request_status SET last_checked_at=@now,
   next_poll_after=@leaseUntil WHERE id IN (SELECT id ... WHERE next_poll_after<=@now AND the link is
   Open+published ... FOR UPDATE OF s SKIP LOCKED LIMIT @batch) RETURNING id`. The claim advances
   `next_poll_after` to a short lease horizon (`LeaseSeconds`) so no other replica re-selects a PR mid-poll and
   a crashed poll becomes due again after the lease expires. Two replicas can never double-claim, which is what
   guarantees the ≤2-calls-per-PR budget. First-sighting upserts a status row for each published Open link
   (`INSERT ... SELECT ... ON CONFLICT (pull_request_link_id) DO NOTHING`) before the claim.
3. **Exactly two GitHub reads per claimed PR:** the PR, then the check-runs for the PR response's head SHA
   (`per_page=100`, single request, truncation flagged). `record.ApplyObservation(...)` decides whether a
   meaningful transition occurred; on Merged/Closed the poller also flips `GitPullRequestLink.Status` in the
   same `SaveChanges` so story #172 fingerprint-reuse frees up.
4. **Backoff & rate-limit handling:** `401`/`403` → sanitized `CredentialsRejected` failure (no audit/event) +
   exponential backoff-with-jitter capped at `MaxBackoffSeconds`; `429` → honour `Retry-After`/`X-RateLimit-Reset`
   into `next_poll_after`, preserving last-known status; timeout/`5xx`/transport → capped backoff-with-jitter.
   Every fault is isolated per-PR (`ChangeTracker.Clear()` + continue) so one poisoned PR never aborts the batch.
5. **Config** (`GitPullRequestStatus` section, DataAnnotations-validated + `ValidateOnStart`): `Enabled`,
   `PollIntervalSeconds` (60–600; prod 300 / dev-CI 60), `BatchSize`, `MaxBackoffSeconds`, `LeaseSeconds`,
   `DegradedAfterMinutes`. No secret-shaped field — the credential resolves through `IGitCredentialProvider`.
6. **Health & metrics:** `GitPullRequestStatusHealthCheck` (Healthy/Degraded only, no live GitHub call) and the
   `Caisson.Ingestion.GitPrStatus` meter.

## Consequences

- The lease is strictly stronger than xmin-CAS: no compare-and-swap retry loop, and no window where two
  replicas both fetch the same PR. The lease horizon must exceed a realistic two-call poll latency; `LeaseSeconds`
  defaults to 120s.
- A record leased then abandoned (crash) is simply re-polled after the lease expires — at-least-once polling,
  which is safe because a poll is idempotent and only meaningful transitions emit audit/events.
- The DB-lease tests exercise the exactly-once claim across two concurrent replicas; the sync-service tests
  exercise the ≤2-call budget, the Merged link dual-write, and the 401/403 + 429 failure paths.
