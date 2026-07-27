# 0002 — Append-only, denormalized-per-snapshot observed state with a stable Rack

## Status
Accepted

## Context
Discovery runs produce point-in-time observations that must be auditable and support drift/history
later. The story's answered design question chose **fully denormalized per snapshot** (duplicate
observed entities each run) over stable inventory entities with observation link tables, for
simplicity and auditability in M0. However, "latest snapshot per rack" (AC3) and rack-level isolation
via `WHERE rack_id = ?` (NFR4) both require a *stable* thing to group by across snapshots.

## Decision
Model the observed graph as **append-only and fully denormalized per snapshot**: each discovery run
writes a new immutable `TopologySnapshot` plus fresh copies of every observed entity, each stamped
with both `snapshot_id` and `rack_id`. Keep exactly one **stable** registry entity, `Rack`
(stable `Id`, unique `ExternalKey`, `Name`, `CreatedAt`), which is *not* snapshot-scoped. Every other
observed entity implements `ISnapshotScoped` (carries `SnapshotId` + `RackId`). Immutability is
enforced in the `DbContext` (a `SaveChanges` override throws if a snapshot-scoped entity is modified).
Latest-snapshot selection is deterministic: `ORDER BY created_at DESC, id DESC`.

## Consequences
- Auditability is trivial: every run is a self-contained, immutable copy; nothing overwrites history.
- Higher storage cost (entities duplicated per run) — acceptable for M0; can optimize to shared
  inventory + observation tables later without changing the query surface much.
- Stable `Rack` gives a deterministic grouping key for "latest per rack" and a clean authorization
  boundary; the rest of the graph stays denormalized as the story requires.
- Redundant denormalized `rack_id`/`snapshot_id` on deep children enable single-predicate isolation and
  indexed joins, at the cost of some write-time duplication.
