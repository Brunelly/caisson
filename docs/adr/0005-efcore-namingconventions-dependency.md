# 0005 — EFCore.NamingConventions dependency

## Status
Accepted

## Context
The story's data-model examples and index list use `snake_case` identifiers
(`rack_id`, `created_at`, `snapshot_id`, `mac_primary`, …), which is idiomatic PostgreSQL. EF Core's
default identifier casing is PascalCase, which would produce quoted mixed-case identifiers that are
awkward to query by hand and diverge from the story's schema. We want consistent `snake_case`
table/column names without hand-annotating every property.

## Decision
Add the `EFCore.NamingConventions` package and call `UseSnakeCaseNamingConvention()` on the Npgsql
options. This rewrites all table, column, key, and index names to `snake_case` automatically.

## Consequences
- All generated identifiers are `snake_case`, matching the story's schema and idiomatic Postgres, with
  no per-property annotations.
- Adds one small, well-maintained dependency to the Infrastructure project (Domain stays clean).
- The convention is applied centrally in `OnConfiguring`/options; a change of convention later would
  require a migration that renames identifiers.
