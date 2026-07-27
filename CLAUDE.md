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

### Driver / HAL abstraction — to come
Device drivers (CHR/RouterOS, Redfish/IPMI) and the hardware-abstraction layer that feeds discovery
are **out of scope for this story** and arrive later. M0 persists observations only; there is no
discovery logic, no device I/O, and no API host here yet. Health/query endpoints are noted for a later
story.

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
