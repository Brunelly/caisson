# 0013 — Discovery orchestration: resumable scheduled & on-demand jobs

Status: Accepted

## Context

Story #8 turns the read-only building blocks — the switch/BMC discovery drivers (#3–#5), the pure
correlation engine (#6), and the persistence bridge + control-plane API host (#7) — into a running
feature: a discovery run for a rack must invoke the drivers, correlate, and persist a snapshot, on
demand *and* on a schedule. The forces:

- **Read-only safety boundary (NFR1, AC5).** The API assembly must keep naming no driver type, so the
  existing non-transitive `ReadOnlyGuardTests.Api_references_no_driver_assembly` invariant stays green.
- **Durability/resumability (AC1, NFR1).** Long, flaky device I/O means completion cannot ride an HTTP
  request; a host restart must resume a run within ~60s and transient failures must retry with bounded
  backoff.
- **One run per rack (NFR5)** enforced under concurrency, and precise overlap semantics (Q1: 409).
- **Minimal M0 rack definition** with **no secrets** — the orchestrator needs to know *what* to discover.
- **Idempotency (AC2)** distinct from the overlap conflict.

## Decision

- **New `Caisson.Orchestration` project** is the single layer allowed to reference `Caisson.Drivers.*`.
  `Caisson.Api` references only `Caisson.Orchestration`; because `Assembly.GetReferencedAssemblies()` is
  non-transitive, the API still names no driver assembly and the guard test is unchanged.
- **Config-bound rack definition** (`Discovery:Racks`) over a DB device schema — the story asks for
  "minimal for M0" and the north-star is git-backed YAML desired-state; config keeps the M0 footprint
  small and swappable, and carries only an **opaque `CredentialsRef`** (never a secret). Deferred: a DB
  device schema / git-YAML source, behind the same `IRackDefinitionProvider` seam.
- **Mutable job entities** (`DiscoveryJob`, `DiscoveryJobStep`, `RackDiscoverySchedule`) alongside the
  append-only snapshot schema. They implement no append-only marker, so `CaissonDbContext.GuardAppendOnly()`
  ignores them and their status transitions in place. They store only counts/diagnostics — never device
  data or secrets (NFR4).
- **DB-durable job runner** (`BackgroundService`) claims work atomically with
  `UPDATE … WHERE id = (SELECT … FOR UPDATE SKIP LOCKED)` so replicas never double-claim, and reclaims a
  crashed `InProgress` job once its heartbeat goes stale. A **partial unique index**
  `discovery_job(rack_id) WHERE status IN ('Queued','InProgress')` is the DB-enforced single-active
  invariant (NFR5), and `discovery_job(rack_id, idempotency_key) WHERE idempotency_key IS NOT NULL`
  backs idempotent replay — both mirroring the story-7 `ux_topology_snapshot_rack_id_version` backstop.
- **Single `IDiscoveryJobService` enqueue seam** shared by the trigger endpoint and the scheduler, so
  the single-active rule has one implementation. A `ux_discovery_job_rack_active` violation → **409**
  carrying the active jobId; a matching `(rackId, idempotencyKey)` → **202 replay** of the existing
  jobId. The two cases are distinct.
- **Step-level resume + idempotent persistence.** The pipeline is four ordered steps (switch discovery →
  BMC discovery → correlation → persistence). Read-only discovery and pure correlation are safe to
  re-run, so resume simply re-executes them; only the **persistence** step is guarded by
  `DiscoveryJob.ResultSnapshotId` so a confirmed snapshot is never written twice. Device observations are
  never persisted in the job record (NFR4), so they are re-read on resume rather than reconstructed.
- **Persistence reuses the story-7 `ITopologySnapshotIngestionService`** (mirroring the mcp-tooling
  `PersistQueryRunner` template) rather than reimplementing persistence; partial device failure →
  `SnapshotStatus.PartialSuccess`.
- **Fixed-interval-plus-jitter scheduling** (Q2), no cron field; `TimeProvider` + an injectable
  `IJitterSource` keep the scheduled path deterministic under test.
- **Cancellation** (Q3): an in-process CTS registry gives the fast path; the durable
  `CancellationRequested` flag, re-read before each step, is the cross-instance source of truth.
- **Deliberate control-plane boundary change:** the read-only boundary is about drivers and
  HTTP-writes-to-devices, not control-plane HTTP verbs. Discovery adds policy-gated non-GET endpoints
  (trigger/cancel/schedule PUT). `ReadOnlyGuardTests` is rescoped: read controllers stay GET-only, and
  every non-GET action is confined to the discovery controllers and carries a `DiscoveryTrigger`/
  `ScheduleManage` policy (fail-closed). `Api_references_no_driver_assembly` is unchanged.
- **RBAC:** `DiscoveryTrigger` = Admin+Operator (trigger/cancel), `TopologyRead` = Admin+Operator+
  ReadOnly+ServiceAccount (all reads), `ScheduleManage` = Admin-only (schedule PUT). Fail-closed via the
  existing fallback policy (anonymous → 401, unrecognised role → 403).

## Consequences

- The API depends on `Caisson.Orchestration` (which transitively pulls the drivers at runtime) but still
  names no driver type — the guard holds. Orchestration is the one place driver access is centralised.
- Config-bound definitions mean adding/rewiring a rack's devices is a config change; migrating to a DB
  or git-YAML source later is isolated behind `IRackDefinitionProvider`.
- A crash in the microsecond between the persistence commit and stamping `ResultSnapshotId` could, on
  resume, create one extra snapshot version; per-rack versioning tolerates this rare, harmless case.
- Background services run in the API host (modular monolith); `RunnerEnabled`/`SchedulerEnabled` options
  allow disabling them (e.g. for deterministic tests).
