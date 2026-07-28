# Contributing to Caisson

Thanks for contributing! This guide covers the branch/PR flow and the local checks that must pass
before a change is merged.

## Branch & PR flow

- Work on a topic branch named `story/<n>-<slug>` or `fix/<slug>`; never commit directly to `main`.
- Keep commits focused and message them clearly (imperative mood, e.g. `Add SwitchPort unique index`).
- Open a Pull Request against `main`. CI (GitHub Actions, Linux) must be green before review.
- Record any non-trivial, hard-to-reverse decision (new dependency, schema shape, cross-cutting
  pattern) as a short ADR under [`docs/adr/`](docs/adr/) in the same PR.

## Before you push

Run the same gates CI runs:

```bash
dotnet tool restore
dotnet build                              # warnings are errors
dotnet format --verify-no-changes         # style / formatting gate
dotnet test tests/Caisson.Domain.Tests    # fast, no database
dotnet test tests/Caisson.Drivers.Abstractions.Tests   # fast, no database
dotnet test tests/Caisson.Infrastructure.Tests   # needs Postgres (see below)
```

`dotnet format` is **required** — if it reports changes, run `dotnet format` (without the flag) and
commit the result.

## Running migrations locally

The design-time factory reads the connection string from `CAISSON_DB`.

```bash
# Start a throwaway Postgres however you like, e.g. with Docker:
docker run --rm -d --name caisson-pg -e POSTGRES_PASSWORD=caisson \
  -e POSTGRES_USER=caisson -e POSTGRES_DB=caisson -p 5432:5432 postgres:16

export CAISSON_DB='Host=localhost;Port=5432;Database=caisson;Username=caisson;Password=caisson'

dotnet ef database update   --project src/Caisson.Infrastructure   # apply
dotnet ef database update 0 --project src/Caisson.Infrastructure   # roll back
```

When you change the domain model or an entity configuration, generate a migration and review the
generated SQL (indexes, constraints, cascade rules, `Down()`):

```bash
dotnet ef migrations add <DescriptiveName> --project src/Caisson.Infrastructure
```

## Running the integration tests

The integration suite prefers an existing database via `CAISSON_TEST_DB`; if that variable is unset it
falls back to Testcontainers (Docker required):

```bash
export CAISSON_TEST_DB='Host=localhost;Port=5432;Database=caisson_test;Username=caisson;Password=caisson'
dotnet test tests/Caisson.Infrastructure.Tests
```

## Coding conventions

- The `Caisson.Domain` project must remain persistence-ignorant: **no** EF Core / Npgsql references,
  no data annotations. Mapping lives entirely in `Caisson.Infrastructure` via Fluent API
  `IEntityTypeConfiguration<T>` classes.
- Nullable reference types and implicit usings are enabled solution-wide; keep the build warning-free.
- Do not add remediation/desired-state fields or any credential/PII fields to the observed-state model.

## Frontend (`web/`) checks

The `angular-build-and-test` CI job runs these; run them before pushing a frontend change:

```bash
cd web
npm ci
npm run lint            # @angular-eslint
npm run format:check    # Prettier, matching the root .editorconfig
npm run build
npm test -- --watch=false   # Vitest/jsdom, headless
```

See [`docs/frontend-getting-started.md`](docs/frontend-getting-started.md) for running the app against
a live `Caisson.Api`, and [ADR 0015](docs/adr/0015-angular-frontend-architecture.md) for the
architecture. Never commit `web/node_modules`, `web/dist`, `web/.angular` or `web/coverage` (already
git-ignored) — never add secrets to `web/src/environments/*.ts`, which is public SPA config only.

## Simulation harness (virtual rack, no physical hardware)

`tests/Caisson.VirtualRack.IntegrationTests` drives the real switch/BMC drivers against in-process
simulators through the real discovery/correlation/persistence path and asserts the result against a
known ground truth:

```bash
dotnet test tests/Caisson.VirtualRack.IntegrationTests -c Release
```

Needs only Postgres (`CAISSON_TEST_DB`, or Docker via Testcontainers) — it skips, rather than fails,
when neither is available. See [`docs/simulation-harness.md`](docs/simulation-harness.md) for the full
local walkthrough (bring-up, seeding, running the API/UI against the seeded rack, expected output, and
troubleshooting) and [ADR 0017](docs/adr/0017-simulation-first-virtual-rack-harness.md) for the design,
including the LLDP fidelity guard that applies if you ever wire a real CHR through a host bridge.

Never enable `Testing:EnableTestAuth` (ADR 0018) outside `Development`/`Testing` — the host refuses to
boot if you do (`TestAuthStartupGuard`) — and never commit it as `true` in any non-Development
`appsettings` file.
