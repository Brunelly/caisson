# Deploying Caisson to the Azure Container Apps TEST environment

This is a **single, TEST-only** deployment target — a place to see the running product. It is **not**
production-grade and never touches production.

## What gets deployed

- **`Caisson.Api`** → Azure Container App `by-azuks-app-caisson-api`, image `ghcr.io/brunelly/caisson-api`.
  Runs with `ASPNETCORE_ENVIRONMENT=Testing`, which activates the environment-gated **test-auth scheme**
  (a fixed, read-only synthetic principal — ADR 0018) and serves **Swagger**. That means the API is
  browsable and queryable **without a real Entra tenant** — deliberately, so we can see it end-to-end
  before the "OIDC via Entra" story lands. Production would run with real Entra OIDC and no test-auth.
- A **virtual rack** is seeded (`Caisson.VirtualRack.Seeder`) so the topology/drift endpoints return
  real demo data.

Once deployed:
- Swagger UI: `https://<api-fqdn>/swagger`
- Latest topology (read-only test-auth): `GET https://<api-fqdn>/api/racks/{rackId}/topology/snapshots/latest`
- Health: `https://<api-fqdn>/health/ready`

## How it is wired (SOC2-aligned)

- **GitHub → Azure = OIDC workload-identity federation** (app registration `by-ea-caisson-dev`, federated
  credential for the `test` environment). No Azure secret is stored in GitHub.
- The **only** GitHub secret is `CAISSON_DB` (the Postgres connection string). Repo **variables**
  `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID` identify the federated identity.
- The Azure SP is scoped **Contributor on `by-rg-caisson-dev` only**.
- Migrations are applied explicitly (`dotnet ef database update`) — the app does **not** migrate on boot.
  The workflow opens the Postgres firewall to the runner's IP for the migrate/seed step and closes it
  again afterwards (`always()`), so the DB is never left open. The running app reaches Postgres via the
  "allow Azure services" firewall rule.

## How to run it

Actions → **Deploy (Test)** → *Run workflow* (it is `workflow_dispatch` only — nothing auto-deploys).
Dev/Test deploys are self-service; a production pipeline (when it exists) stays gated on a human approver.

## The web SPA (demo)

The Angular SPA is deployed to `by-azuks-app-caisson-web` from `ghcr.io/brunelly/caisson-web`, built with
the **`azuredemo`** configuration: the same auth-bypass as the `e2e` build (a fake OidcSecurityService
instead of a real Entra flow) pointed at the deployed API. This is a **deliberate, signed-off TEST demo**
so the UI can be browsed read-only without a real Entra tenant — production would use real Entra OIDC (a
separate story) and drop the bypass. The API's CORS `AllowedOrigins` is set to the SPA origin so the
cross-origin XHR/SignalR calls are allowed.

## ghcr pull is durable

Both `caisson-api` and `caisson-web` packages are **public**, so the container apps pull them
**anonymously** — no registry credential is stored on the apps (an ephemeral `GITHUB_TOKEN` cred would
expire and break later autoscale/cold-start pulls). Keep the packages public, or switch to a long-lived
read:packages credential if they ever need to go private.

## Known follow-ups

- Custom domains / TLS beyond the default `*.azurecontainerapps.io` FQDN are not set up.
- Container apps use single-revision mode; `migrate-seed` re-seeds a fresh virtual rack on every run
  (harmless — it just adds another rack).
