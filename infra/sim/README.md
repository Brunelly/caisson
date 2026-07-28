# Simulation harness: local Postgres + Redis

This compose file provisions only the two stateful dependencies the virtual-rack harness needs —
**PostgreSQL 16** and **Redis 7**, both pinned by tag+digest (NFR2). The MikroTik RouterOS and Redfish/
IPMI simulators are deliberately **not** containers: they run in-process inside the .NET test/seeder
host (`tests/Caisson.Drivers.Simulators`), which is faster, fully deterministic, and already the
established CI pattern. See [`docs/adr/0017-simulation-first-virtual-rack-harness.md`](../../docs/adr/0017-simulation-first-virtual-rack-harness.md)
for the rationale, and [`docs/simulation-harness.md`](../../docs/simulation-harness.md) for the full
contributor walkthrough (harness → API → UI, expected output, troubleshooting).

## Usage

```bash
docker compose -f infra/sim/docker-compose.yml up -d
docker compose -f infra/sim/docker-compose.yml ps        # both services healthy
docker compose -f infra/sim/docker-compose.yml down -v   # tear down, including the data volume
```

## Ports and credentials

| Service  | Port | Credentials                                   |
|----------|------|------------------------------------------------|
| Postgres | 5432 | `caisson` / `caisson`, database `caisson`      |
| Redis    | 6379 | none (no `requirepass` — local dev/test only)  |

These are fixed, non-secret, local-only test credentials — the same ones the GitHub Actions `postgres:16`
service container already uses. No source edits are required to use them; see
[`.env.example`](.env.example) for every environment variable a contributor may want to export.

## Next steps

With both services up, run the seeder to populate a rack and drive a discovery job:

```bash
export CAISSON_DB="Host=localhost;Port=5432;Database=caisson;Username=caisson;Password=caisson"
dotnet ef database update --project src/Caisson.Infrastructure --startup-project src/Caisson.Infrastructure
dotnet run --project tests/Caisson.VirtualRack.Seeder -c Release
```

Then follow [`docs/simulation-harness.md`](../../docs/simulation-harness.md) to run the API and UI
against the seeded rack.
