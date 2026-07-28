# Frontend getting started: running the Angular UI against a live API

This is the operator-facing companion to [ADR 0015](adr/0015-angular-frontend-architecture.md): how to
run `Caisson.Api` locally, get a topology snapshot into it, and point the Angular app (`web/`) at it.

## 1. Run PostgreSQL and Caisson.Api

```bash
# A throwaway Postgres, e.g. with Docker (or use any Postgres 16 instance you have):
docker run --rm -d --name caisson-pg -e POSTGRES_PASSWORD=caisson \
  -e POSTGRES_USER=caisson -e POSTGRES_DB=caisson -p 5432:5432 postgres:16

export CAISSON_DB='Host=localhost;Port=5432;Database=caisson;Username=caisson;Password=caisson'

dotnet tool restore
dotnet ef database update --project src/Caisson.Infrastructure

dotnet run --project src/Caisson.Api
```

By default this listens on the Kestrel dev ports printed at startup (typically
`https://localhost:5001` / `http://localhost:5000`) — match `apiBaseUrl`/`hubUrl` in
`web/src/environments/environment.ts` (see step 3) to whichever you use.

Swagger/OpenAPI is available at `/swagger` outside Production, useful for exploring the read endpoints
directly while wiring up the frontend.

## 2. Get a topology snapshot into your rack

The API is strictly read-only from the outside; a snapshot only exists once a discovery run against a
**registered rack** persists one. Two things need to exist first:

1. **A `Rack` row** in the database — the stable registry entity discovery and the API key off
   (`Rack.ExternalKey`). There is no seed script shipped for local dev; insert one directly, matching
   the shape used by the API integration test seed
   (`tests/Caisson.Api.IntegrationTests/SeedData.cs`), e.g. via `dotnet ef` or a short throwaway script
   that calls `context.Racks.Add(new Rack(Guid.NewGuid(), "<external-key>", "<display name>",
   DateTime.UtcNow))` and saves.
2. **A `Discovery:Racks` entry** in configuration (`RackDefinitionOptions`, section `Discovery`) whose
   `ExternalKey` matches that rack, listing the switches/servers to discover — see
   [`docs/routeros-discovery.md`](routeros-discovery.md) and [`docs/redfish-discovery.md`](redfish-discovery.md)
   for the connection-option shape (vendor/model/connection kind/host/port/credentials ref).

> **Note on simulators.** To exercise the UI against real discovery output without physical hardware,
> use the story-#11 simulation-first virtual-rack harness instead of the steps above: it seeds a rack
> and drives a real discovery job against in-process MikroTik/Redfish simulators for you — see
> [`docs/simulation-harness.md`](simulation-harness.md). The manual `Discovery:Racks` config route above
> is still the one to use against *real*, reachable network devices (a MikroTik switch and a
> Redfish/IPMI BMC).

With both in place, trigger an on-demand run (Admin/Operator role required) and poll for completion:

```bash
curl -X POST https://localhost:5001/api/racks/<rackId>/discovery-jobs \
  -H "Authorization: Bearer <token>" -H "Content-Type: application/json" -d '{}'

curl https://localhost:5001/api/racks/<rackId>/discovery-status \
  -H "Authorization: Bearer <token>"
```

Once `discovery-status` shows a `Succeeded` job, `GET /api/racks/<rackId>/topology/snapshots/latest`
returns the snapshot the Angular app will render.

## 3. Configure and run the Angular app

`web/src/environments/environment.ts` (development) holds only non-secret, public SPA config — a
PKCE public client needs no client secret, so nothing here is sensitive, but nothing here should ever
be a *real* production value either:

| Key | Meaning |
|---|---|
| `apiBaseUrl` | Base URL of the running `Caisson.Api` (step 1). |
| `hubUrl` | The SignalR hub endpoint, `${apiBaseUrl}/hubs/topology`. |
| `oidc.authority` | Your Entra tenant's OIDC authority — the same one `AzureAd:Authority` in `Caisson.Api`'s config points at. |
| `oidc.clientId` | The SPA's (public, PKCE) app registration client id. |
| `oidc.redirectUri` / `oidc.postLogoutRedirectUri` | Must match a redirect URI registered on that app registration (defaults to the dev server root). |
| `oidc.scope` | Must request the API's exposed scope so the access token is issued for the right audience (`AzureAd:Audience`). |

Then:

```bash
cd web
npm ci
npm start   # ng serve, defaults to http://localhost:4200
```

Navigate to `http://localhost:4200/racks/<rackId>/topology`. You'll be redirected through your Entra
tenant's login; once authenticated with a role listed in `Caisson.Api.Security.CaissonRoles.All`
(Admin, Operator, ReadOnly, ServiceAccount), the page loads the latest snapshot, subscribes to live
updates over SignalR, and lets you search and drill into entities.

## Everyday frontend checks

The same gates CI runs (see `.github/workflows/ci.yml`, `angular-build-and-test` job):

```bash
cd web
npm run lint
npm run format:check
npm run build
npm test -- --watch=false
```

See [ADR 0015](adr/0015-angular-frontend-architecture.md) for the architecture rationale (D3 rendering,
no NgRx, client-side search, live-update strategy) and `docs/live-topology-events.md` for the SignalR
wire contract `TopologySignalRService` implements.
