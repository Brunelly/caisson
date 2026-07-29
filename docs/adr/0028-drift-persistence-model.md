# 0028 — Drift persistence model: mutable upsert, not append-only

## Status

Accepted

## Context

Story #64 (M1) computes drift between a rack's latest desired-state revision (#62/#63) and its latest
observed topology snapshot (M0) and must persist the result with history (AC3): a recompute of the
identical `(rackId, desiredRevisionId, observedSnapshotId)` tuple updates the existing report in place
(no duplicate rows), while a new revision or new snapshot produces a new report and prior reports remain
queryable. Every other persisted entity in this codebase to date — `TopologySnapshot`,
`TopologyEntityDiff`, `DesiredStateVersion` — is append-only, enforced generically by
`CaissonDbContext.GuardAppendOnly()` via the `IAppendOnly`/`ISnapshotScoped` marker interfaces. Drift
reports are the first entity in this schema that must be genuinely updatable after insert.

## Decision

- **`DriftReport`/`DriftItem` implement NEITHER `IAppendOnly` NOR `ISnapshotScoped`.** They are mutable,
  upsertable registry rows — the same shape as `Discovery.DiscoveryJob` (also mutable, also excluded
  from the append-only guard). `GuardAppendOnly()` therefore never blocks the in-place `Modified`
  transition AC3 requires. The drift *audit trail* stays append-only: `DriftComputationService` writes a
  `drift.report.computed` `TopologyAuditEvent` in the same atomic `SaveChangesAsync` as every (re)compute,
  so drift activity itself remains tamper-evident even though the report row is not.
- **`DriftReport` is keyed by a UNIQUE `(RackId, DesiredRevisionId, ObservedSnapshotId)` index** — the
  idempotency/upsert key AC3 requires. A recompute for the same tuple is a find-or-insert followed by an
  in-place scalar-field update (`ComputedAtUtc`, `TotalItems`, `CountsBySeverityJson`, etc.); the identity
  fields never change once a row exists.
- **`DriftItem` uses a surrogate PK plus a separately content-hashed `DriftItemId`**, with uniqueness
  scoped to `(DriftReportId, DriftItemId)` rather than global — see ADR 0029 for why the hash formula
  makes global uniqueness incorrect. Deleting a `DriftReport` cascades its `DriftItem` rows (the
  retention pruner, story #64 AC/NFR5, only needs to delete report rows).
- **FKs to the append-only upstream rows are `Restrict`**, mirroring `TopologyEntityDiffConfiguration`'s
  precedent for its "to" snapshot: a `DesiredStateVersion`/`TopologySnapshot` referenced by a drift report
  cannot be deleted out from under it. `RackId` is `Restrict` on both tables, matching
  `DiscoveryJobConfiguration`'s treatment of the stable `Rack` registry entity.
- **No optimistic concurrency token.** Unlike `DiscoveryJob` (which uses the Npgsql `xmin` shadow property
  to defend against two runner instances racing on the same claimed job), drift recompute is a pure,
  deterministic function of its inputs (`DriftEngine`, ADR forthcoming) — two concurrent recomputes of the
  identical tuple always converge to the identical output, so a last-writer-wins update is safe by
  construction. Concurrent *inserts* of the same new tuple are handled by catching the unique-violation
  and retrying as an update (mirrors `DesiredStateIngestionService`/`TopologySnapshotIngestionService`'s
  existing race-handling shape), not by locking.
- **Migration `AddDriftPersistence` is additive-only** (two new tables, no changes to existing tables or
  triggers) and applies/rolls back/reapplies cleanly.

## Consequences

- A reader of this schema expecting drift reports to be append-only history rows (as every prior
  persisted entity has been) will not find that here — this ADR is the record of that deliberate
  divergence, justified by AC3's explicit in-place-update requirement.
- Because `DriftReport`/`DriftItem` are mutable, any future direct-SQL tooling must not assume the
  database-level append-only triggers (`caisson_reject_append_only_mutation`) protect these two tables —
  they deliberately do not, and a negative test (`Caisson.Infrastructure.Tests`) asserts they are excluded
  from `GuardAppendOnly()`.
- Retention pruning (hybrid: last 200 reports per rack, max 180 days, both configurable) can delete
  `DriftReport` rows directly via a bounded query and rely on `Cascade` to remove their `DriftItem` rows,
  without a separate item-level delete pass.
