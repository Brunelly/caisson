# 0015 — Angular frontend architecture: live rack topology map (story #10)

Status: Accepted

## Context

Story #10 is the **first frontend** in the repo: an Angular UI that renders the observed rack topology
(server → NIC → switch port → VLAN) as an interactive graph, supports search and drill-down, and stays
live via the story-9 SignalR hub. It consumes the story-7 read-only query APIs and must stay strictly
read-only (M0), enforce the same four-role RBAC model as the API (`Caisson.Api.Security.CaissonRoles`),
and put no secrets in the client bundle. There is no existing frontend in the repo to match conventions
against, so this ADR fixes the baseline future frontend work should follow.

## Decision

- **Location and toolchain.** The app lives at `web/`, generated with the Angular CLI
  (`ng new --routing --style=scss --strict`) on the current Angular release available at the time of
  this story (Angular 22). Angular 22 standalone components are the CLI default — there are no
  `NgModule`s in this codebase. `web/` is a sibling of `src/`/`tests/`, not nested inside them, so its
  own `node_modules`/`dist`/`.angular`/`coverage` are excluded from the **root** `.gitignore` (added in
  the same commit as `web/package.json`, ADR discipline for repository-bloat avoidance) as well as the
  CLI-generated `web/.gitignore`.
- **Layering mirrors the backend's layering discipline** (ADR 0001), translated to frontend idiom:
  `core/` (auth, HTTP interceptor, telemetry — cross-cutting, app-wide), `topology/` (the one feature
  module: page, graph, search, details panel, legend, services, state, live), `shared/` (design tokens,
  reusable presentational components like the status badge). Feature code depends on `core`/`shared`,
  never the reverse.
- **Linting/formatting**: `@angular-eslint` via `ng add @angular-eslint/schematics`, which on this
  Angular/ESLint version generates a flat `eslint.config.js` (ESLint's current default; there is no
  `.eslintrc.json` on this toolchain) plus Prettier, both matching the root `.editorconfig`'s
  `[*.{ts,html,scss}]` 2-space/LF block added alongside.
- **Unit test runner: Vitest, not Karma.** `ng new` on this Angular CLI version scaffolds the
  `@angular/build:unit-test` builder with Vitest + jsdom as the default runner (Karma/Jasmine is no
  longer the default). `ng test` runs headless-by-default in a non-TTY environment (`--watch` defaults
  to `false` outside a TTY), so CI needs no `--browsers=ChromeHeadless` flag and no Chrome install step —
  a deliberate deviation from the older Karma-based convention this story was originally scoped against.
- **Graph rendering: raw D3, a manual layered (columnar) layout + `d3-zoom`, not `d3.hierarchy`.** The
  story's explicit technical constraint is "prefer simple, widely-used Angular graph rendering (e.g.
  D3) without heavy licensing constraints." D3 is Apache-clause-free (ISC-licensed) with no licensing
  risk, and a layered layout (one column per entity type: Server → NIC → Switch → Port → VLAN) is the
  natural fit for the DAG's inherent layering (a force layout would fight it). It deliberately does not
  use `d3.hierarchy`/`d3.tree`, because the graph is not a strict tree: a switch port and a VLAN can each
  be reached from more than one NIC (they are dedup-derived from every NIC's best attachment, not
  one-per-NIC), which breaks `d3.hierarchy`'s single-parent assumption — positions are computed directly
  per column instead.
  D3 is wrapped by a thin Angular component that owns the `<svg>` and patches it via D3 enter/update/exit
  joins, not torn down and rebuilt, so pan/zoom/selection
  state survives a live refresh.
- **State: plain injectable services + Angular signals/RxJS, no NgRx.** This is a single read-only page
  with one meaningful piece of shared state (the current rack's snapshot/graph/selection/connection
  status). The backend's own minimalism (no CQRS/mediator framework, plain services — ADR 0001) is the
  precedent; NgRx's action/reducer/effect ceremony buys nothing here and would be the first dependency
  this story adds without a forcing requirement.
- **Auth: `angular-auth-oidc-client`, code+PKCE, in-memory token storage.** Configured against the same
  Entra tenant/app registration as the API's `AzureAd:Authority`/`Audience` (a public SPA client needs no
  secret under PKCE). The token is held in memory, not `localStorage`, to reduce the XSS blast radius for
  sensitive MAC/topology data (NFR3) — a token in memory is lost on hard refresh and re-acquired via
  silent renew, an accepted trade-off for a lower-sensitivity-surface read-only viewer.
- **Search: client-side, no new backend endpoint.** No `/topology/search` endpoint exists and no task in
  this story builds one. The resolved sizing question caps a rack at ≤20 servers/≤96 ports/≤200 VLAN
  edges (medium-rack), so the full graph is already resident in the client after the initial load;
  indexing and filtering it in-memory is simpler and faster than a network round-trip per keystroke, and
  is deliberate rather than an oversight.
- **Live strategy: snapshot-refetch-on-event**, per the story's own resolved question. On
  `SnapshotUpdated` the client refetches `snapshots/latest` + the graph via REST (never trusting the
  event's summary as authoritative) and patches the rendered graph in place — simpler and more robust
  than streaming/applying partial diffs, at the cost of an extra round trip per update (acceptable at
  this data scale).
- **Backend consequences carried by this story** (implemented ahead of the frontend, story #10 step 1):
  a config-driven, named CORS policy (`Cors:AllowedOrigins`, never `AllowAnyOrigin`, only
  `appsettings.Development.json` seeding `http://localhost:4200`) was added to `Caisson.Api/Program.cs`
  because none existed; and `TopologyGraphProjector`/`NicNodeDto` gained a nullable
  `UnmappedReasonCode`, because the projector previously dropped an unmapped NIC's reason-code candidate
  row entirely, which would have made AC3 ("unmapped NIC shows a reason code/message") impossible to
  satisfy from the wire contract.

## Consequences

- Future frontend contributions follow this layout/toolchain rather than re-deriving one; deviations
  (Vitest instead of Karma, flat ESLint config) are intentional and match the CLI's current defaults, not
  oversights.
- No NgRx dependency to maintain; if a future story needs cross-page shared state or complex undo/replay
  semantics, that is the trigger to revisit this decision, not before.
- In-memory token storage means a hard page refresh always re-runs the OIDC silent-renew/redirect flow;
  acceptable for a read-only internal tool, but worth knowing if a future story adds a long-form editing
  flow with unsaved state.
- Client-side search will not scale past the medium-rack cap without a backend `/topology/search`
  endpoint; if a future story raises the size cap, this decision must be revisited.
- The CORS and `UnmappedReasonCode` backend changes are additive and non-breaking, but any other
  consumer of `NicNodeDto` picks up a new field it should tolerate being `null`.
