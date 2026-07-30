# Simulation harness: run the whole virtual rack locally

This is the contributor-facing companion to [ADR 0017](adr/0017-simulation-first-virtual-rack-harness.md):
how to stand up a fully simulated rack — MikroTik RouterOS switch + Redfish/IPMI BMC, discovered by the
real drivers through the real orchestration pipeline — and reach a rendered topology view, with no
physical hardware, in under 15 minutes.

Use `/` as the live application's entry point. The `/__dev-harness__/topology/{rackId}` route is
fixture-backed and does not validate rack discovery, live topology REST, or SignalR integration.

## 1. Start Postgres + Redis

```bash
docker compose -f infra/sim/docker-compose.yml up -d
docker compose -f infra/sim/docker-compose.yml ps   # wait until both show healthy
```

See [`infra/sim/README.md`](../infra/sim/README.md) and [`infra/sim/.env.example`](../infra/sim/.env.example)
for ports, credentials, and every environment variable the harness reads. Redis is optional (enables
live topology updates across multiple `Caisson.Api` instances); everything below works without it.

## 2. Migrate and seed the virtual rack

```bash
export CAISSON_DB="Host=localhost;Port=5432;Database=caisson;Username=caisson;Password=caisson"

dotnet tool restore
dotnet ef database update --project src/Caisson.Infrastructure --startup-project src/Caisson.Infrastructure

dotnet run --project tests/Caisson.VirtualRack.Seeder -c Release
```

The seeder boots the in-process MikroTik/Redfish simulators (`tests/Caisson.Drivers.Simulators`) from
the single ground-truth fixture (`tests/Caisson.VirtualRack.Fixtures`), registers a rack, drives a real
discovery job through `AddCaissonOrchestration`'s real `RouterOsSwitchDriverFactory`/
`RedfishBmcDriverFactory` to `Succeeded`, and prints:

```
E2E_RACK_ID=<a guid>
E2E_SEARCH_TERM=vrack-srv1
E2E_SEARCH_LABEL_PART=vrack-srv1
```

Keep the printed `E2E_RACK_ID` — you'll use it in step 4. The simulators exit with the seeder process;
the persisted snapshot in Postgres is all the API needs from here on.

## 3. Run Caisson.Api

You can run the API either against a real Entra tenant (see
[`docs/frontend-getting-started.md`](frontend-getting-started.md)) or, for the fastest local loop, with
the same environment-gated test-auth scheme CI uses (ADR 0018) so no OIDC setup is needed at all:

```bash
export ASPNETCORE_ENVIRONMENT=Testing
export Testing__EnableTestAuth=true
export ASPNETCORE_URLS=http://localhost:5000
export Cors__AllowedOrigins__0=http://localhost:4200

dotnet run --project src/Caisson.Api -c Release
```

`Testing:EnableTestAuth` MUST NOT be set outside `Development`/`Testing` — `TestAuthStartupGuard` makes
the host refuse to boot otherwise (see ADR 0018). Watch the startup log for the prominent
`TEST-AUTH SCHEME ACTIVE` warning confirming it's on.

## 4. Run the Angular UI against it

```bash
cd web
npm ci
npm run build:e2e
npm run serve:e2e   # serves dist/web/browser at http://localhost:4200 with SPA fallback
```

Navigate to `http://localhost:4200/racks/<E2E_RACK_ID>/topology` (the id printed in step 2). The `e2e`
build configuration substitutes a fake `OidcSecurityService` that satisfies the client-side role guard
without a real Entra tenant — the live API's test-auth scheme accepts the resulting request regardless
of the token's content (see [ADR 0018](adr/0018-environment-gated-test-auth-scheme.md)).

Alternatively, `npm start` (`ng serve`, the real `development` configuration with real OIDC) works too
if you already have an Entra tenant configured per `docs/frontend-getting-started.md` — just make sure
`Testing:EnableTestAuth` is off (the default) in that case.

## Expected output

The seeded rack's topology graph shows exactly:

- **One server** (`vrack-srv1`) with **three NICs**.
- **One High-confidence mapping** (`eth0` → switch port `ether1`), with reason codes including
  `MacLearnUnique` **and** `LldpConsistent` — proof the LLDP round trip was genuinely exercised, not
  stubbed (see the fidelity guard below).
- **One ambiguous mapping** (`eth1`, two switch-port candidates on `ether2`/`ether3`).
- **One unmapped NIC** (`eth2`, reason `NotSeenInSwitch`).
- **One unmapped switch port** (`ether4`, no owning NIC).

This is `tests/Caisson.VirtualRack.Fixtures/VirtualRackDefinition.cs`'s ground truth — see
`ExpectedTopologyBuilder`/`TopologyDiff` for the exact expectation the automated harness
(`tests/Caisson.VirtualRack.IntegrationTests`) asserts against.

## Running the automated harness directly

```bash
dotnet test tests/Caisson.VirtualRack.IntegrationTests -c Release
```

Needs only Docker (for its own isolated Postgres via Testcontainers, or set `CAISSON_TEST_DB` to point
at the compose Postgres above) — no other setup. It skips (not fails) when Postgres is unavailable.

## Drift detect/apply/rollback/RBAC E2E suite (story #68)

A dedicated, CI-proof suite building on the same in-process simulators, seeded with a deterministic
single-port access-VLAN drift instead of the happy-path topology above:

```bash
dotnet test tests/Caisson.VirtualRack.IntegrationTests -c Release --filter "FullyQualifiedName~Drift"
```

Four test classes, all in `tests/Caisson.VirtualRack.IntegrationTests`:

- **`DriftEndToEndTests`** — real discovery against a mismatched desired revision yields exactly the
  expected `AccessVlanMismatch` (+ the fixture's already-seeded ambiguity item); severity/subject-detail
  determinism and `driftItemId` stability across a repeated recompute; the `/drift/latest` and
  `/drift/reports/{driftReportId}` read contracts (what the Angular UI's data path actually consumes);
  and a harness-supplied correlation id reaching the discovery-job audit trail.
- **`DriftApplyEndToEndTests`** — apply success through the REAL `RouterOsSwitchMutatingDriver`: the job
  reaches `Completed`/`Applied`, the in-process simulator independently confirms the device was mutated,
  a fresh discovery closes the loop (drift resolved), and both the `drift.apply.job.created`/`.completed`
  audit rows carry full before/after/actor/correlation detail with no credentials.
- **`DriftApplyRollbackEndToEndTests`** — the orchestration-level auto-rollback proof: a scripted
  withheld-confirmation driver (registered only for one rack, under a distinct vendor descriptor) mutates
  real simulator state, the job reaches `Failed`/`AutoRolledBack` with exactly one device call, and a
  fresh discovery shows the port reverted to its ORIGINAL VLAN with the drift item still present. See
  [ADR 0035](adr/0035-drift-apply-e2e-ci-suite-and-rollback-proof-split.md) for why this is deliberately
  separate from the driver-level rollback proof (`SetAccessVlanIntegrationTests`, in
  `Caisson.Drivers.MikroTik.IntegrationTests`) rather than duplicating it.
- **`DriftApplyRbacEndToEndTests`** — an Operator lacking the `DriftApply` role gets `403`, creates no
  job, and produces an `authorization.forbidden` audit event (see
  [ADR 0036](adr/0036-forbidden-authorization-audit-event.md)); plus the NFR5 concurrency proof — two
  concurrent applies for the same `driftItemId` yield one job and exactly one device write.

Every rack these tests create resolves to a SEPARATE, stateful, write-capable switch simulator instance
(`RouterOsProfileRenderer.RenderStateful`) — the original happy-path simulator used by every other
detection-only test is never mutated. Because that write-capable simulator's port state is shared across
every device-mutating test in the same run (tests within one xUnit collection run sequentially, but in an
order that is not guaranteed to stay fixed across runs), each such test calls
`VirtualRackApiFactory.ResetSwitchPortAccessVlanForTest` before seeding rather than assuming what an
earlier test left behind.

In CI this runs as its own named, filtered step (`Drift detect/apply/rollback/RBAC E2E tests
(simulators)`) immediately after the broad virtual-rack step, uploading `drift-e2e.trx` as an isolated
artifact on failure — see `.github/workflows/ci.yml`'s `build-and-test` job.

## The LLDP fidelity guard

Because the switch simulator's LLDP neighbour table (`/ip/neighbor/print`) and bridge/MAC table
(`/interface/bridge/host/print`) are populated from the **same** ground-truth definition, and the
simulators are in-process loopback processes (no Linux host bridge in the path), LLDP is genuinely
exercised end-to-end — the happy-path assertion requires `LldpConsistent` in the mapped NIC's reason
codes, so a regression to MAC-only correlation fails the test.

**If you ever wire a real MikroTik CHR through a Linux/Proxmox-style host bridge** (a virtual "cable" as
a two-endpoint bridge) instead of the in-process simulator, this fidelity guard does **not** carry over
automatically: Linux bridges drop the `01:80:c2:00:00:0e` reserved-multicast LLDP destination by
default, so LLDP never crosses the wire and the switch reports zero neighbours — correlation silently
degrades to MAC-only. You **must** set `group_fwd_mask 0x4000` (LLDP; add `0x0004` for LACP too) on any
bridge used as a sim cable, or your setup will look correlation-complete while quietly never exercising
the LLDP path at all.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| `docker compose ... up -d` fails to bind 5432/6379 | A port conflict with a local Postgres/Redis. Stop it, or edit the `ports:` mapping in `infra/sim/docker-compose.yml`. |
| Seeder hangs or times out waiting for the job | Check `docker compose -f infra/sim/docker-compose.yml ps` — Postgres must be `healthy`. Confirm `CAISSON_DB` matches the compose credentials. |
| `Caisson.Api` throws at startup mentioning `Testing:EnableTestAuth` | You set the flag under an environment other than `Development`/`Testing` — `TestAuthStartupGuard` (ADR 0018) refuses to boot. Set `ASPNETCORE_ENVIRONMENT=Testing` (or `Development`). |
| Browser shows a CORS error calling the API | `Cors:AllowedOrigins` doesn't include the Angular origin — set `Cors__AllowedOrigins__0` to match whatever serves the Angular build (e.g. `http://localhost:4200`). |
| SignalR shows "disconnected"/stale banner | Expected without Redis if you have multiple API instances; single-instance works with no Redis. Check the `/hubs/topology/negotiate` request in devtools for a 401/403 — that means the bearer token isn't reaching the hub. |
| `curl http://localhost:5000/health/ready` returns non-200 | The API can't reach Postgres — confirm the compose stack is up and `CAISSON_DB` is correct. |
| Topology shows MAC-learned mappings but no `LldpConsistent` reason on the clean NIC | See the fidelity guard above — you're likely pointed at a real CHR through a host bridge with LLDP silently dropped, not the in-process simulator. |

## See also

- [ADR 0017](adr/0017-simulation-first-virtual-rack-harness.md) — the "one definition, two renderers"
  design and why the simulators run in-process rather than as containers.
- [ADR 0018](adr/0018-environment-gated-test-auth-scheme.md) — the environment-gated test-auth scheme.
- [`docs/frontend-getting-started.md`](frontend-getting-started.md) — running the Angular UI against a
  real Entra tenant instead of the test-auth scheme.
- `.github/workflows/ci.yml`'s `angular-e2e-smoke` job — the same loop, fully automated in CI.
