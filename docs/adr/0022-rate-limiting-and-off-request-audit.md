# 0022: Per-subject rate limiting and an off-request-path, channel-backed audit writer

## Status

Accepted

## Context

Security review `security-review-5` (finding #5) found two related gaps: nothing in the host used
`AddRateLimiter`/`UseRateLimiter`, so no endpoint — including the discovery trigger/cancel control-plane
writes — was throttled; and every auditable read performed a synchronous `INSERT` into
`topology_audit_event` on the request path, into a table protected by a `BEFORE UPDATE OR DELETE` trigger
(ADR 0011) with no retention/pruning path anywhere in the repo. Together, an unthrottled caller could
both generate unbounded read load and grow an un-prunable table without bound.

## Decision

- **Rate limiting**: `AddRateLimiter`/`UseRateLimiter` (placed immediately after `UseAuthorization()`, so
  the partition key — the authenticated `oid` claim — is reliably present) with a generous global fixed
  window (600/min per subject) and a materially tighter window (20/min per subject) layered on the
  discovery trigger/cancel endpoints via `[EnableRateLimiting(RateLimitPolicies.DiscoveryTrigger)]`.
  `/health/*` is explicitly `.DisableRateLimiting()` — a load balancer/orchestrator's frequent,
  unauthenticated probing must never be throttled.
- **Off-request-path audit**: `ChannelAuditEventWriter` (the new default `IAuditEventWriter`) captures a
  plain `AuditWriteRequest` — actor/correlation resolution happens synchronously, since both depend on
  request-scoped state gone by the time the background writer's own DI scope runs — and enqueues it to a
  bounded `Channel<AuditWriteRequest>` (`DropWrite` on saturation, never blocking the request).
  `AuditEventBackgroundWriter` (a `BackgroundService`) drains and batches into the audit table off the
  request path, coalescing repeated identical reads from the same principal+correlation id within one
  ~500ms flush window, and drains any remaining queued events in `StopAsync` before the process exits.
- `TopologyHub.UnsubscribeFromRack` now tracks per-connection subscribed rack ids and skips both the
  (already-no-op) group removal and the audit write when the connection was never actually in that group.
- No retention/partitioning path is implemented in this pass — the append-only trigger continues to
  reject every `UPDATE`/`DELETE`/`TRUNCATE` unconditionally (ADR 0011, hardened further in finding #6).

## Consequences

- Audit writes are now **eventually consistent**, not synchronously durable: a write enqueued just before
  an unclean process crash (not a graceful `StopAsync`) can be lost, and a saturated channel silently
  drops the newest event (logged as a warning). This is a deliberate trade-off traded for taking the
  insert off the read path's latency budget.
- A future retention path (monthly partitions with DETACH/archive, or an age-based exception carved into
  the append-only trigger) is still an open follow-up — noted here rather than attempted in this pass, to
  keep this story's scope to hardening the write pattern itself.
- The rate limiter's per-subject partitioning means an authenticated caller's budget is independent of
  every other caller's; an unauthenticated caller falls into a single shared "anonymous" partition — not
  a concern in practice, since the fallback authorization policy already rejects it with 401 before the
  rate limiter's own 429 could apply.
