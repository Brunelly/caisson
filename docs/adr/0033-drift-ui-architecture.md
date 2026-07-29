# 0033 — Drift UI architecture

## Status

Accepted

## Context

Story #67 adds the first Angular UI on top of story #64's drift-query API and story #65's drift-apply
write path: a topology-map overlay for drifted ports, a drift-report list/detail view, and a
permission-gated Apply workflow with live job status. It must extend the existing M0 topology stack
(ADR 0015: standalone components, signals not NgRx, `ApiResult`/`toApiResult`, a single SignalR hub
connection, reconnect/poll degrade) rather than fork it, and must not regress the read-only M0 map.

Several structural choices needed to be made up front:

1. **Module boundary.** Drift could live inside `topology/` (it renders on the topology map and
   references topology entities) or as a sibling feature module.
2. **Routing scope.** The backend drift-query API is entirely rack-scoped (`/api/racks/{rackId}/drift/
   ...`); there is no cross-rack listing endpoint.
3. **Deriving "apply status" for the list view.** `DriftItemDto` (Caisson.Api.Contracts.DriftContracts)
   carries no status/applyJobId field — only the drift-apply job list does, keyed by `driftItemId`.
4. **Client idempotency for the Apply POST.** The story text's own illustrative example mentions an
   idempotency key; ADR 0032 already gives the server durable, idempotent-create semantics
   (`Created`/`ExistingActiveJob`, a partial-unique-index-backed dedup) with no client-supplied key in
   the actual `ApplyDriftCorrectionRequest` contract.
5. **Severity → visual mapping.** The existing `StatusBadgeComponent`/status tokens (`--color-status-
   confirmed/-ambiguous/-unmapped`) encode topology *mapping confidence*, where High confidence is
   "good" (green). `DriftSeverity` is the opposite polarity: High severity is "bad".
6. **Live updates for drift-apply jobs.** ADR 0032 already decided the backend relays
   `DriftApplyJobStatusChangedEvent` over the same `TopologyHub`/`/hubs/topology` connection and
   per-rack group as `SnapshotUpdated`/`DiscoveryJobStatusChanged` — no new hub.

## Decision

1. **New sibling module `web/src/app/drift/`**, not folded into `topology/`. Drift is a distinct concern
   (its own read API, its own write path, its own RBAC permission) that happens to *reference* topology
   entities (switch ports) for one overlay layer; the overlay/topology-graph integration is additive
   (`topology/model/drift-topology-overlay.ts` consumed by `topology-graph.component.ts`), not a reason
   to merge the modules. This mirrors how `topology/` itself is a sibling of `core/`/`shared/`, not
   nested inside another feature.
2. **Rack-scoped-only routing.** Every drift route (`racks/:rackId/drift`, `.../drift/items/:driftItemId`,
   `.../drift/jobs/:jobId`) is nested under the known `rackId`, exactly like the existing `racks/:rackId/
   topology` route — there is no rack-picker or cross-rack drift list, because no API supports one.
3. **Derive the list's status column by joining the drift-apply job list.** The list view calls both
   `DriftReportService.getReportById(...)` (items) and `DriftApplyService.getJobs(rackId, {...})` (jobs),
   indexes the newest job per `driftItemId` client-side, and renders the joined `DriftApplyJobStatus` (or
   "—" when no job exists). This is the only way to surface AC2's status column without a backend change,
   and keeps `DriftReportStateService` in the same signals-only shape as `TopologyStateService`.
4. **No client idempotency key.** The Angular apply workflow relies entirely on ADR 0032's server-side
   dedup (one active job per drift item) plus a client-side disable-until-settled guard (`submitting`
   signal set synchronously before the HTTP call) to prevent double-submit. `ApplyDriftCorrectionRequest`
   has exactly one field (`driftItemId`); inventing a client idempotency key the contract doesn't accept
   would be dead code, not defence in depth.
5. **Severity gets its own, independent token mapping**, not a reuse of `StatusBadgeComponent`'s
   confidence-band `BadgeKind`. `drift/shared/drift-severity-badge.component.ts` maps `High` → the *same*
   `--color-status-unmapped`/`-bg` tokens (red) used for topology's worst confidence band, `Medium` →
   `--color-status-ambiguous`/`-bg` (amber), `Low` → `--color-text-muted` on `--color-bg-elevated`
   (neutral) — reusing existing tokens (never inventing new ones) but with severity-correct polarity:
   binding `High` directly to the *confidence* badge's "good/green" case would invert the semantics.
   Every severity always renders icon + text, never colour alone (NFR5).
6. **Live drift-apply status reuses the existing `TopologySignalRService`/hub connection.** A
   `DriftApplyJobStatusChanged` handler is added alongside the existing `SnapshotUpdated`/
   `DiscoveryJobStatusChanged` handlers on the same `HubConnection`, gated through the same
   `applyIfNewer` watermark store (a new `driftApplyJobStreamKey(jobId)`, since the event carries no
   `eventId` — dedup keys on `${jobId}:${seq}`), and degrades to REST polling via the same
   `connectionStatus()`/reconnect machinery already shown on the topology page. Components never read the
   raw hub event; they read a new root `DriftApplyJobStatusService` signal store, mirroring
   `DiscoveryStatusService`'s relationship to the hub.

## Consequences

- `drift/` depends on `topology/model` (graph model, stable-key helpers) and `topology/services` (
  `ApiResult`) and extends `topology/live/topology-signalr.service.ts` and
  `topology/graph/topology-graph.component.ts` directly — these two files are the only places outside
  `drift/` this story touches, keeping the M0 read-only surface's own files otherwise untouched.
- Because there is no cross-rack drift endpoint, a future "drift across my fleet" view is out of scope
  until the backend adds one; this UI does not attempt to fake it client-side by fanning out per-rack
  calls.
- The status-column join means the list view issues two requests per page (items + jobs) instead of one;
  acceptable at the 500-item/NFR2 scale this story targets, but a future story adding a status field
  directly to `DriftItemDto` would let the list drop the second call.
- The severity token mapping is deliberately duplicated (not derived) from the confidence-band mapping —
  a maintainer changing `StatusBadgeComponent`'s colours must remember severity has its own, separate
  mapping with inverted polarity.
- Rollback-window duration display (AC4) is deferred to ADR 0034, which also covers the CDK dialog/toast
  primitives this workflow needs.
