# Caisson

Caisson is the control-plane service for **observed-state rack topology discovery**. It persists
read-only snapshots of what has been *observed* about a rack — switches, ports, servers, NICs, MAC
addresses, VLANs, and LLDP neighbours — together with the ambiguity (reason codes + confidence
scoring) that inevitably arises when correlating evidence from BMC inventory and switch bridge/LLDP
tables.

This repository delivers the **Milestone M0** persistence foundation: a persistence-ignorant domain
model and a code-first PostgreSQL schema (EF Core + Npgsql) with query-oriented indexes.

> **Scope note.** M0 is *read-only discovery*. The model deliberately contains **no** remediation or
> desired-state fields, **no** credentials or PII, and **no** device drivers, discovery logic, or API
> endpoints — those arrive in later stories. See [`CLAUDE.md`](CLAUDE.md) for the architecture record.

## Solution layout

```
Caisson.sln
├── src/
│   ├── Caisson.Domain           Pure C# observed-state model (entities, enums, value objects).
│   │                            Zero EF Core / Npgsql references so it stays shareable and AOT-clean.
│   └── Caisson.Infrastructure   EF Core 8 + Npgsql: DbContext, entity configurations, migrations.
└── tests/
    ├── Caisson.Domain.Tests           Fast, DB-free unit tests for domain invariants.
    └── Caisson.Infrastructure.Tests   Integration tests against a real PostgreSQL instance.
```

The observed graph is **append-only and fully denormalized per snapshot**: every discovery run writes
a fresh `TopologySnapshot` and a fresh copy of every observed entity, each stamped with `snapshot_id`
and `rack_id`. `Rack` is the one **stable** registry entity, so "latest snapshot for a rack" is
deterministic. See [ADR 0002](docs/adr/0002-append-only-denormalized-snapshots-with-stable-rack.md).

## Prerequisites

- .NET SDK **8.0.423** (pinned via [`global.json`](global.json)).
- A reachable PostgreSQL instance for running migrations and integration tests.

## Configuration

Connection strings are read from environment variables — **no secrets are committed**:

| Variable          | Used by                                   | Example                                                              |
| ----------------- | ----------------------------------------- | ------------------------------------------------------------------- |
| `CAISSON_DB`      | design-time factory (`dotnet ef …`)       | `Host=localhost;Port=5432;Database=caisson;Username=caisson;Password=caisson` |
| `CAISSON_TEST_DB` | integration tests (preferred over Docker) | same shape, pointing at a throwaway test database                   |

If `CAISSON_TEST_DB` is unset, the integration suite falls back to spinning up PostgreSQL via
[Testcontainers](https://dotnet.testcontainers.org/) (requires Docker).

## Build / test / migrate

```bash
# Restore the pinned local tools (dotnet-ef) once per checkout.
dotnet tool restore

# Build the whole solution (warnings are errors).
dotnet build

# Verify formatting.
dotnet format --verify-no-changes

# Fast domain unit tests (no database needed).
dotnet test tests/Caisson.Domain.Tests

# Integration tests (needs CAISSON_TEST_DB or Docker).
dotnet test tests/Caisson.Infrastructure.Tests

# Apply the schema to a database, then roll it back (round-trip check).
export CAISSON_DB='Host=localhost;Port=5432;Database=caisson;Username=caisson;Password=caisson'
dotnet ef database update           --project src/Caisson.Infrastructure
dotnet ef database update 0         --project src/Caisson.Infrastructure

# Add a new migration after model changes.
dotnet ef migrations add <Name>     --project src/Caisson.Infrastructure
```

## Documentation

- Architecture decisions: [`docs/adr/`](docs/adr/)
- Contributing guide: [`CONTRIBUTING.md`](CONTRIBUTING.md)
- Architecture record for AI/human contributors: [`CLAUDE.md`](CLAUDE.md)

## License

Licensed under the [Apache License 2.0](LICENSE).
