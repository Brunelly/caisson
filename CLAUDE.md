# Caisson — architecture record for contributors (human & AI)

This file captures the initial architectural decisions for Caisson so future contributors follow the
grain of the codebase. Formal, individually-numbered decisions live in [`docs/adr/`](docs/adr/); this
is the narrative overview.

## What Caisson is

Caisson is the **control-plane persistence layer** for observed-state rack topology discovery
(Milestone M0, read-only). It stores versioned snapshots of what discovery *observed* about a rack and
the ambiguity involved in correlating that evidence.

## Architectural decisions

### Modular monolith with strict layering
- `Caisson.Domain` — persistence-ignorant POCOs (entities, enums, value objects). **Zero** EF Core /
  Npgsql references, no data annotations. This keeps the model shareable with the future appliance
  agent and clean for AOT scenarios.
- `Caisson.Infrastructure` — EF Core 8 + Npgsql. Owns the `DbContext`, per-entity Fluent API
  configurations, value converters, and migrations.
- See [ADR 0001](docs/adr/0001-modular-monolith-and-layering.md).

### This layer is standard EF Core, NOT NativeAOT
The control-plane persistence layer is a normal ASP.NET Core / EF Core application. The NativeAOT
appliance agent is a *separate future component*; we favour AOT-friendly patterns in `Caisson.Domain`
(so its types can be reused) but do not constrain the Infrastructure layer to AOT.

### Driver / HAL abstraction
`Caisson.Drivers.Abstractions` defines the read-only discovery driver contracts —
`ISwitchDiscoveryDriver` and `IBmcDiscoveryDriver`, both living in the `ReadOnly` namespace as an
explicit, compile-time-visible safety boundary: no method there may write, configure, or power-cycle
a device, enforced by a reflection guard test that fails the build if a mutation-verb method ever
appears. Driver calls return a structured `DriverResult<T>` (success/failure + optional per-item
`DriverDiagnostic`s reusing `Caisson.Domain.Enums.ReasonCode`) instead of throwing for expected
failures, and drivers are resolved by `DriverDescriptor` (vendor/model/connection kind) through a
non-reflective, DI-populated registry. See
[ADR 0006](docs/adr/0006-readonly-driver-abstraction-and-registry.md) and
[docs/adding-a-driver.md](docs/adding-a-driver.md). This story is **abstraction-only**: concrete
MikroTik RouterOS and Redfish/IPMI implementations are future stories (#4/#5). M0 still persists
observations only; the discovery **pipeline/orchestrator** now exists in `Caisson.Orchestration`
(story #8, below). The read-only control-plane **API host** and the correlation→persistence bridge
exist (story #7, below).

### Simulation-first testing
Discovery will be validated against simulators (CHR + a Redfish simulator) with repeatable test data
volumes. For this persistence story that principle shows up as: fast DB-free domain unit tests, plus
integration tests that provision an ephemeral PostgreSQL (an existing `CAISSON_TEST_DB`, else
Testcontainers) and exercise the real migration.

### Append-only, denormalized-per-snapshot observed state, with a stable Rack
Every discovery run writes a new immutable `TopologySnapshot` and a fresh copy of every observed
entity, each carrying `snapshot_id` **and** `rack_id`. Nothing in a snapshot is mutated in place; the
`DbContext` enforces this by throwing if a snapshot-scoped entity is modified. `Rack` is the single
**stable** registry entity (stable `Id` + unique `ExternalKey`), which is what makes "latest snapshot
per rack" deterministic and rack-level isolation (`WHERE rack_id = ?`) straightforward.
See [ADR 0002](docs/adr/0002-append-only-denormalized-snapshots-with-stable-rack.md).

### Deterministic "latest snapshot" selection
Latest snapshot is `ORDER BY created_at DESC, id DESC` — `created_at` is the primary key of the sort
and the `Guid` `id` only breaks exact-timestamp ties. Older snapshots remain fully queryable.

### MAC normalization rule
MAC addresses are stored **normalized**: lowercase hex, 12 characters, no separators. The
`MacAddressValue` value object parses any of `:` / `-` / `.` / bare and mixed-case input to that
canonical form and offers `ToDisplay()` for the colon-grouped presentation form. Storage is always the
normalized form; presentation formatting is a UI/API concern.

### Confidence-bounds rule
Correlation confidence is a `ConfidenceScore` value object bounded to `[0.0, 1.0]` (rejects `<0`, `>1`,
and `NaN`). The bound is enforced twice: in the value object factory and again by a PostgreSQL `CHECK`
constraint on the column. See [ADR 0004](docs/adr/0004-mac-and-confidence-value-objects-and-check-constraints.md).

### Topology correlation engine
Correlation lives in a separate **pure** project, `Caisson.Correlation` (layered like the driver
abstraction: AOT-compatible, no EF Core/Npgsql/HTTP, enforced by a reflection guard). It consumes the
story-3 driver info records and reuses the domain `ConfidenceScore`/`MacAddressValue`/`ReasonCode` (the
enum is extended append-only, ≤32 chars) to infer NIC↔port↔VLAN mappings with rule-based additive scoring,
explicit ambiguity (ranked candidates, LAG detection), unmapped NIC/port reasons, and access-vs-trunk
disambiguation. It is deterministic (stable ordering/scores) and side-effect free — resolve it via
`AddTopologyCorrelation()`. Confidence bands are High ≥ 0.8 / Medium 0.5–0.79 / Low < 0.5. See
[ADR 0010](docs/adr/0010-topology-correlation-engine.md) and
[docs/topology-correlation.md](docs/topology-correlation.md).

### Correlation → persistence bridge, per-entity diffs, and audit trail
Story #7 persists each discovery run as an immutable, versioned `TopologySnapshot` (monotonic per-rack
`Version`, `TriggerType`, run timing) together with durable per-entity diffs (`TopologyEntityDiff`,
computed by stable key between consecutive snapshots) and a tamper-evident audit trail
(`TopologyAuditEvent`). The bridge lives in `Caisson.Infrastructure` (`Persistence/Ingestion`,
`Persistence/Queries`, `Persistence/Shaping`) — a `ProjectReference` to the pure `Caisson.Correlation`,
not a separate application project. The mapper/differ/graph-projector/cursor codec are pure and DB-free;
the `TopologySnapshotIngestionService` is the only DbContext-touching piece and does a single atomic
`SaveChangesAsync` (NFR3). Both new entities implement `IAppendOnly`: the `DbContext` guard blocks
update **and** delete, and a DB trigger on `topology_audit_event` enforces it against raw SQL (NFR4).
Canonical stable keys are defined once in `Caisson.Domain.Topology.Diffing.StableKeys`. See ADR 0011.

### Read-only control-plane API host
`Caisson.Api` (ASP.NET Core, `Microsoft.NET.Sdk.Web`) is the M0 control plane: strictly **read-only**,
GET-only topology/audit query endpoints under `/api/racks/{rackId}/topology/...`. It references only
`Caisson.Infrastructure` (never any `Caisson.Drivers.*`, enforced by a guard test — NFR1). RBAC uses
JWT/Entra OIDC with a config-driven group/role → canonical-role mapping (Admin/Operator/ReadOnly/
ServiceAccount); anonymous → 401, authenticated-without-a-role → 403. Correlation-id middleware honours
/generates `X-Correlation-Id`, echoes it, and enriches every Serilog line; ProblemDetails for 400/404;
`/health/live` and `/health/ready` (DB probe); OpenAPI/Swagger. See ADR 0012.

### Discovery orchestration (drivers → correlation → persistence)
`Caisson.Orchestration` is the **one layer allowed to reference `Caisson.Drivers.*`**: it drives a
rack's read-only discovery, feeds the output through the pure correlation engine, and persists via the
story-7 ingestion service. `Caisson.Api` references only `Caisson.Orchestration`; because
`Assembly.GetReferencedAssemblies()` is non-transitive, the API still names no driver assembly and the
`Api_references_no_driver_assembly` guard is unchanged. Discovery runs are modelled as durable,
resumable, idempotent `DiscoveryJob`s (mutable, **not** append-only) with per-step `DiscoveryJobStep`
rows; a `DiscoveryJobRunner` `BackgroundService` claims work atomically (`FOR UPDATE SKIP LOCKED`),
retries transient driver failures with bounded backoff, heartbeats each step, and resumes crashed jobs.
A partial-unique index enforces one active job per rack (409 on overlap), a second backs idempotent
replay (202). A `DiscoveryScheduler` enqueues fixed-interval-plus-jitter runs through the **same**
`IDiscoveryJobService`. The rack definition is config-bound (`Discovery:Racks`) with opaque credential
refs — no secrets. Role-gated non-GET APIs (trigger/cancel/schedule) live only on the discovery
controllers. See ADR 0013.

### EF Core + Npgsql, code-first migrations, snake_case
PostgreSQL via Npgsql; schema is code-first through EF Core migrations. Column/table names are
`snake_case` via `EFCore.NamingConventions`. See
[ADR 0003](docs/adr/0003-ef-core-npgsql-code-first-migrations.md) and
[ADR 0005](docs/adr/0005-efcore-namingconventions-dependency.md).

## Guardrails (M0)
- **No** remediation/desired-state fields (no `Desired*`, `Target*`, VLAN/port *config intent*).
- **No** credentials or PII in the observed-state schema (NFR5). A reflection guard test enforces this.
- Natural-key uniqueness is always **scoped to the snapshot** (e.g. `(snapshot_id, serial)`), never
  global — a new snapshot legitimately repeats the same serials/MACs.
