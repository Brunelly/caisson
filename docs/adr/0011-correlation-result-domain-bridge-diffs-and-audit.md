# 0011 — Correlation-result → domain bridge, per-entity diffs, and audit trail

## Status
Accepted

## Context
Story #7 must persist each discovery run as an immutable, versioned observed-state snapshot, compute and
store per-entity diffs between consecutive snapshots, and record a tamper-evident audit trail — building
on the story-2 append-only EF domain, the story-3 driver info records, and the story-6 pure
`TopologyCorrelationResult` (string-keyed mappings/ambiguities/unmapped NICs+ports with confidence bands
and reason codes). The story answered two questions that this ADR fixes: snapshot storage is the
**hybrid** shape (normalized per-entity rows the story-2 model already provides, with indexed stable keys
for diffing) and the **canonical stable keys** per entity type. It must also satisfy: NFR3 transactional
all-or-nothing persistence, NFR4 tamper-evident audit, and AC2 diffs that are queryable *without*
recomputation and idempotent on re-persist.

## Decision
- **The bridge lives in `Caisson.Infrastructure`, not a new application project.** A `ProjectReference`
  to the pure `Caisson.Correlation` is added (no layering/AOT rule forbids it; Correlation stays EF-free).
  New folders: `Persistence/Ingestion` (mapper, differ, ingestion service), `Persistence/Queries`
  (read helpers), `Persistence/Shaping` (graph projector, cursor codec). This matches the existing
  query/config layout and keeps the change small.
- **Guids are minted at persist time** via an injected `ITopologyIdGenerator` (`Func<Guid>` in the pure
  pieces). Determinism is a property of the correlation engine only (ADR 0010), not of the persisted
  snapshot; injecting the id source keeps the mapper/differ unit-testable with reproducible output.
- **Canonical stable keys** (the story's answered question) are defined once in
  `Caisson.Domain.Topology.Diffing.StableKeys` and used identically by the mapper, differ and query
  layer so keys never drift: Switch = serial ?? management IP; SwitchPort = `{switchKey}|{portName}`;
  Server = BMC UUID ?? hostname ?? BMC address; NIC = normalized MAC; VLAN = VLAN id; MAC =
  `{mac}|{source}`; LLDP = `{chassisId}|{portId}`.
- **`ReasonCodes[0]` is the persisted primary reason** on `TopologyCandidateMapping` — the engine does
  not guarantee significance ordering, so the full reason list plus VLANs and confidence band are kept in
  the bounded (≤ 8192) `EvidenceJson`. A confident mapping → one candidate row; an ambiguous mapping → N
  rows ordered by descending confidence; an unmapped NIC → a row with a null switch port.
- **Unmapped ports are persisted as ordinary `SwitchPort` rows with no incoming candidate mapping**
  (surfaced via the graph query's anti-join), rather than widening `TopologyCandidateMapping.NicId` to
  nullable. This keeps the NIC-anchored invariant on the already-accepted story-2 entity intact and is
  the smaller change.
- **Snapshots carry a stored, indexed, monotonic per-rack `Version`** (unique `(rack_id, version)`
  index), assigned as `max(version)+1` inside the ingestion transaction with a single retry on the
  unique-violation race — rather than computing a version at read time. AC1 and the data model require a
  persisted monotonic version.
- **A durable `TopologyEntityDiff` entity** (snapshot-scoped, `SnapshotId` = the "to" snapshot, nullable
  `PreviousSnapshotId`) sits alongside the coarse per-snapshot `TopologyChangeSummary` rollup, so diffs
  are queryable without recomputation (AC2). A unique `(snapshot_id, entity_type, entity_stable_key)`
  index is the idempotency backstop; the pure `TopologyDiffCalculator` omits unchanged entities and
  treats a first snapshot as all-Added.
- **Tamper-evidence is enforced twice** (NFR4). A new `IAppendOnly` marker is implemented by
  `TopologyEntityDiff` and `TopologyAuditEvent`; the `DbContext` guard blocks both **update and delete**
  for `IAppendOnly` (stronger than the update-only rule for snapshot content, which still allows
  retention deletes). In addition, the migration installs a Postgres `BEFORE UPDATE OR DELETE` trigger on
  `topology_audit_event` so raw SQL is rejected too — satisfying NFR4's "enforced via database
  constraints".
- **`TopologySnapshotIngestionService` is the only DbContext-touching piece** and does a single atomic
  `SaveChangesAsync` (snapshot graph + MAC rows + diffs + change summary + a discovery audit event) →
  NFR3 all-or-nothing. It is a library seam for the future story-8 orchestrator and is wired to **no**
  HTTP endpoint.

## Consequences
- The mapper, differ, graph projector and cursor codec are pure and DB-free, so the mapping/diff/shaping
  behaviour is fully unit-tested with **no** database (Docker-free), satisfying the codegen-env
  constraint; DB-backed tests prove atomicity, forced-failure rollback, monotonic-version uniqueness and
  stored-diff = calculator-diff idempotency.
- Standalone `Mac` rows and switch-learning churn are intentionally **not** diffed (a NIC's stable key is
  its MAC, so NIC-level identity already captures MAC changes); this avoids noise and keeps the diff set
  aligned with the navigation graph the differ can see.
- A MAC-less NIC cannot be represented (the domain `Nic` requires a MAC, which is also its stable key), so
  the mapper skips it; the engine already surfaces it as an unmapped NIC. Follow-up if MAC-less NIC
  identity is ever needed.
- The append-only guard now distinguishes snapshot content (update-blocked, delete-allowed) from
  `IAppendOnly` records (update- and delete-blocked); the DB trigger makes audit tamper-evidence hold
  even outside EF.
