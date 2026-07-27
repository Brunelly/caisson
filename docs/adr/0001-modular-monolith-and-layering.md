# 0001 — Modular monolith and layering

## Status
Accepted

## Context
Caisson starts as a single control-plane service but must remain easy to reason about and to extend
with later milestones (drivers/HAL, discovery, query APIs). The observed-state model also needs to be
reusable by a future NativeAOT appliance agent, which cannot take a dependency on EF Core or Npgsql.
We need a structure that isolates persistence concerns from the domain model without prematurely
splitting into microservices.

## Decision
Adopt a modular monolith with strict layering. `Caisson.Domain` holds persistence-ignorant POCOs
(entities, enums, value objects) with **zero** EF Core / Npgsql references and no data annotations.
`Caisson.Infrastructure` references `Caisson.Domain` and owns all persistence: the `DbContext`,
per-entity Fluent API configurations, value converters, and migrations. Tests are split to mirror the
layers (`Caisson.Domain.Tests`, `Caisson.Infrastructure.Tests`).

## Consequences
- The domain model stays clean and shareable (including with AOT components) because all mapping lives
  in Infrastructure via `IEntityTypeConfiguration<T>`.
- A compile-time boundary (no EF package in Domain) prevents accidental persistence leakage.
- Slightly more ceremony: mapping is expressed in Fluent API rather than attributes on the entities.
- Future modules (drivers, discovery, API host) slot in as additional projects without reshaping the
  domain.
