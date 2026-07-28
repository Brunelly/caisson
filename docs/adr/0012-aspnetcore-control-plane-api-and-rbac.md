# 0012 — ASP.NET Core control-plane API host and RBAC

## Status
Accepted

## Context
Story #7 exposes read-only, role-based query APIs (latest snapshot, snapshot history, snapshot detail,
topology graph, entity detail/history, drift, and audit trail) for the Angular UI and operators. No API
host existed yet (CLAUDE.md deferred it to "a later story"). The endpoints must be **strictly read-only**
(NFR1: no driver calls, no discovery triggers, GET-only), enforce Admin/Operator/ReadOnly/ServiceAccount
via OIDC/Entra role claims without a custom identity system, apply correlation-ids and structured logging
to every request (AC5, NFR5), and return problem-details for errors (AC3). Secrets must stay out of
source.

## Decision
- **A new greenfield `Caisson.Api` host** (`Microsoft.NET.Sdk.Web`, inheriting `net8.0` +
  warnings-as-errors) referencing **only** `Caisson.Infrastructure` (Domain/Correlation transitively) and
  explicitly **no** `Caisson.Drivers.*` assembly. A reflection guard test asserts the API references no
  driver assembly and that every controller action is `[HttpGet]` (NFR1).
- **Attribute-routed controllers** (over minimal-API groups) under
  `/api/racks/{rackId}/topology/...` and `/api/racks/{rackId}/audit` — more standard for Swagger/RBAC at
  this scale with no existing in-repo minimal-API pattern to match. Responses are `Contracts/` DTOs
  projected via the pure shaping functions (graph projector, cursor codec). Pagination is an opaque
  base64 `(createdAt|id)` cursor continuing the deterministic `SnapshotSelector` ordering; invalid
  pageSize/cursor → 400 `ValidationProblemDetails`, missing rack/snapshot/entity → 404 problem-details.
- **AuthN via JWT bearer against Entra ID/OIDC**, Authority/Audience from config, `RoleClaimType=roles`,
  no custom identity store. An `IClaimsTransformation` maps Entra group/app-role claims to the canonical
  `Admin/Operator/ReadOnly/ServiceAccount` roles via a config-driven `Authentication:RoleMappings`
  dictionary. **AuthZ**: a fallback policy requires an authenticated user (anonymous → 401) and a named
  `TopologyRead` policy requires one of the read roles (authenticated-without-a-role → 403), applied to
  every controller.
- **Correlation-id middleware** honours a valid inbound `X-Correlation-Id`, generates one when
  absent/invalid, stashes it on a scoped `ICorrelationContext`, pushes it into the Serilog `LogContext`
  (so every log line carries it), and echoes it in the response header.
- **Serilog structured logging** (compact JSON, `UseSerilogRequestLogging`) — named in the project
  guidelines and preferred over the built-in JSON console. `AddProblemDetails` + `UseExceptionHandler` +
  `UseStatusCodePages`; Swashbuckle/OpenAPI with a JWT bearer security definition; `/health/live` (self)
  and `/health/ready` (Npgsql DB probe).
- **API-access audit rows are written** per auditable read (so the audit endpoint returns discovery
  **and** API-access events, AC3), kept to a single insert on the indexed append-only table behind an
  `IAuditEventWriter` seam so it can be made off-request later if the NFR2 P95 < 500 ms budget is
  threatened.
- **No secrets in source**: the connection string is read from `CAISSON_DB` / `ConnectionStrings:Caisson`
  (env/Key Vault) exactly as the design-time factory does; `appsettings*.json` hold only non-secret
  placeholders (Authority/Audience, empty connection string, empty role mappings).

## Consequences
- `Program` exposes `public partial class Program` so `WebApplicationFactory<Program>` can host the API in
  integration tests; a test-only `TestAuthHandler` injects configurable role claims (no real Entra
  tenant), and the RBAC matrix, correlation-id, pagination and problem-details behaviours are proven
  end-to-end against an ephemeral Postgres.
- The strictly read-only boundary is defended by the reflection guard (no driver reference, GET-only
  actions); the ingestion seam that *could* write is deliberately not registered against any endpoint.
- Problem-details bodies carry the RFC 7807 shape (`title`, `status`, `detail`); the media type may be
  `application/json` rather than `application/problem+json` under MVC content negotiation — the body
  contract is what callers depend on.
- The audit writer trades a small per-read insert for a complete access trail; if latency ever becomes a
  concern it can move off the request path behind the existing seam without changing the endpoint
  contract.
- Health/OpenAPI endpoints are `AllowAnonymous`; all topology/audit endpoints require a read role.

## Review-driven refinements
- **Keyset pagination uses the full composite `(timestamp, id)` predicate**, not timestamp-only. The
  cursor already encoded both halves; the page queries now filter
  `ts < cur.ts OR (ts == cur.ts AND id < cur.id)`, matching the `(… desc, id desc)` ordering. Without
  this, audit rows sharing an `OccurredAtUtc` (a discovery event plus several same-tick API-access reads)
  were silently dropped at a page boundary — a compliance-relevant loss. `KeysetPosition` carries the
  decoded position from `RequestPaging` into the query layer.
- **The entity route binds the stable key with a catch-all** (`entities/{entityType}/{**stableKey}`)
  because SwitchPort/LLDP keys legitimately contain `/` (e.g. a port id `Ethernet1/0/1`); a single-segment
  binding 404'd those entities. A catch-all must be terminal, so entity history moved from a
  `/history` suffix to `entities/{entityType}/history/{**stableKey}` — the only contract shape change,
  acceptable pre-adoption in M0.
- **Swagger/OpenAPI is gated to non-production environments** (`!IsProduction()`) rather than served
  unauthenticated everywhere, so a hosted control plane does not disclose its full API surface to any
  network-reachable client.
- Shared read-endpoint concerns (keyset page trimming, validation-error → 400, rack-not-found 404) live on
  a small `ReadOnlyControllerBase` so the three controllers no longer duplicate them.
