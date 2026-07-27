# 0003 — EF Core + Npgsql, code-first migrations

## Status
Accepted

## Context
The observed-state schema targets PostgreSQL (Azure Database for PostgreSQL Flexible Server in
production, ephemeral Postgres in CI). Schema changes must be deterministic, reviewable, and
reproducible on Linux CI runners (NFR3), and the control-plane is a .NET service.

## Decision
Use Entity Framework Core 8 with the `Npgsql.EntityFrameworkCore.PostgreSQL` provider in a **code-first
migrations** workflow. The domain model plus per-entity `IEntityTypeConfiguration<T>` classes are the
source of truth; `dotnet ef migrations add` generates the SQL, which is committed and applied via
`dotnet ef database update`. `dotnet-ef` is pinned as a local tool (`.config/dotnet-tools.json`). The
design-time factory reads its connection string from the `CAISSON_DB` environment variable so no
secrets are committed. PostgreSQL-specific features used include `jsonb` columns (bounded evidence /
change-count payloads), `integer[]` array columns (observed tagged VLANs), partial unique indexes, and
`CHECK` constraints.

## Consequences
- Schema is versioned in source control and applied identically in dev and CI; migrations can be
  applied and rolled back (`database update 0`), satisfying the CI round-trip requirement.
- Ties the persistence layer to PostgreSQL semantics (jsonb, arrays, partial indexes) — acceptable and
  intended; portability to other RDBMSs is a non-goal.
- Contributors must regenerate a migration and review its SQL whenever the model changes.
