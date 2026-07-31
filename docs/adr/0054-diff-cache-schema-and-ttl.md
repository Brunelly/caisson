# 0054 — Impact-preview diff cache schema and TTL

## Status

Accepted

## Context

Story #171 caches the computed impact-preview diff per candidate so repeated previews and PR submission do
not recompute (AC2; NFR1). The answered design question fixes storage as a **PostgreSQL table with TTL
cleanup** (preferred for simplicity/audit) over Redis or a hybrid. Forces:

- The cache must be **rack-scoped and leak-safe** (NFR2): a cache row must never be retrievable across
  racks, and a new baseline revision must invalidate stale previews.
- Identical candidate content against the same baseline must **dedupe** to a single row (AC2), and the
  returned `candidateId` must be stable across cache hits.
- Expired rows must be **swept** without a heavyweight job, and the sweep must be able to DELETE rows —
  which the append-only `DbContext` guard would otherwise block.

## Decision

1. **New `desired_state_candidate_diff_cache` table** backed by a mutable POCO
   `DesiredStateCandidateDiffCache` with private setters that **does NOT implement `IAppendOnly`**, so the
   `DbContext.GuardAppendOnly` sweep permits the TTL delete. Columns: `id`, `rack_id`,
   `baseline_revision_id`, `candidate_sha256`, `baseline_sha256`, `raw_unified_diff` (text, length-bounded),
   `structured_summary_json` (jsonb, bounded), `created_at_utc`, `expires_at_utc` (nullable), `created_by`,
   plus an `xmin` concurrency token — mirroring `RackNetworkIntentConfiguration`.
2. **Cache key = unique index on `(rack_id, baseline_revision_id, candidate_sha256)`.** This is the
   leak-safe, rack-scoped, content-addressed key that dedupes per content, prevents cross-rack retrieval
   (NFR2), and correctly invalidates when a new baseline revision arrives. A second non-unique index on
   `(rack_id, expires_at_utc)` backs the pruner.
3. **The cache row's `Id` IS the `candidateId`.** The speculative separate `DesiredStateCandidate`
   authoring table from the story's data model is deferred (YAGNI — no persisted candidate-authoring entity
   exists). GET resolves the row by `(id, rack_id)`.
4. **TTL via a `DesiredStateDiffCachePruner : BackgroundService`** copying `DriftRetentionPruner`'s
   `PeriodicTimer`/`TimeProvider` shape with an internal `TickAsync` for deterministic tests. It
   `ExecuteDeleteAsync`s rows `WHERE expires_at_utc < now` in bounded batches, is gated by
   `DesiredState:DiffCache` options (`Enabled`/`PollSeconds`/`TtlMinutes`/`PruneBatchSize`), and is
   registered as a hosted service. `ExpiresAtUtc` is stamped `CreatedAtUtc + TtlMinutes` at insert.

## Consequences

- The migration is code-first via the design-time factory and round-trips (up/down) on ephemeral Postgres.
- Because the cache is mutable and not append-only, it is NOT tamper-evident — acceptable: it is a
  recomputable cache, not an audit record. The audit trail (counts + hashes + cacheHit only, never the
  YAML/diff body) remains the append-only source of truth.
- A `null` `expires_at_utc` means "never expire" and is skipped by the pruner; today every insert stamps a
  TTL, so this is a forward-compatible escape hatch, not a live path.
