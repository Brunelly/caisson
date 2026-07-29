# 0016: A dev/CI-only UI harness route for real-browser accessibility/interaction testing

## Status

Accepted

## Context

Story #10's Angular UI is gated end-to-end by OIDC/Entra (`roleGuard`, `provideCaissonAuth`), and
`Caisson.Api`'s JWT bearer validation has no test/mock issuer configured — by design, only the
in-process `WebApplicationFactory` suite (`TestAuthHandler`) can mint an accepted token. That leaves
`web/e2e/topology.smoke.spec.ts` (Task #55) unable to run anywhere without a real Entra tenant and a
seeded backend, which no local dev machine or this project's CI has today (its job is intentionally
gated behind an `E2E_ENABLED` repo variable an external harness sets).

That is a real gap: several things can only be checked in an actual browser, not `vitest`/`jsdom` —
`topology-page.a11y.spec.ts` already disables its `color-contrast` axe rule specifically because jsdom
has no paint engine, deferring that check to "the Playwright e2e a11y pass" — but no such pass existed.
Real focus management (`document.activeElement`), the CDK overlay's transparent-backdrop outside-click
behaviour, and keyboard-driven ARIA state all behave differently under jsdom's approximation than in a
real DOM/renderer.

## Decision

Add a dev/CI-only route, `/__dev-harness__/topology/:rackId`, that renders the *real*
`TopologyPageComponent` (and every real child: search, graph, legend, details panel) with route-scoped
providers that fake only the wire — `TopologySnapshotService`, `DiscoveryStatusService`,
`TopologyEntityService`, `OidcSecurityService.getAccessToken`, and the SignalR `HUB_CONNECTION_FACTORY`
seam already used by `TopologySignalRService`'s own unit spec — returning fixture data with confirmed/
ambiguous/unmapped states. `TopologyStateService`/`TopologySignalRService` are re-provided with
`useClass` at the same route-scoped environment injector so their internal `inject()` calls resolve the
fakes rather than bubbling to the root injector's real HTTP-backed services. A `web/e2e/
topology-harness.spec.ts` Playwright spec drives it in a real Chromium browser: dropdown open/close
(selection, outside click, Escape)/keyboard operability/focus-return/ARIA, drill-down candidates/reason
codes/unmapped reasons/history, live-update patch-in-place and reconnect/stale-banner handling (via the
fake hub), and `@axe-core/playwright` with `color-contrast` **enabled**, run once in light and once in
dark theme.

The harness route is excluded from production entirely via the same `fileReplacements` mechanism
already used for `environment.ts`/`environment.prod.ts` (a runtime `environment.production` ternary was
tried first and rejected — bundlers don't reliably fold a property read off an imported object into a
dead branch, so the route/fixtures/fake-hub code stayed reachable in the shipped bundle). `angular.json`'s
`production` configuration now also replaces `app.routes.ts` with `app.routes.prod.ts`, which omits the
harness route/import outright.

## Addendum (security-review-5, finding #7): isolate the harness build from the real-backend smoke build

Originally `angular-e2e-smoke` built ONE `e2e`-configured Angular bundle (OIDC auth bypass, no route
swap) and served it for BOTH `topology.smoke.spec.ts` (real, seeded `Caisson.Api` + Postgres backend)
and `topology-harness.spec.ts` (fixture data, fake SignalR hub, no backend). That meant the only build
ever served alongside a real backend also shipped the unauthenticated `/__dev-harness__/...` route and
its fixture/fake-hub code — broader reachable surface than a real deployment would ever have, for no
reason the smoke spec needed (it only ever visits the real `/racks/:rackId/topology` route).

The `e2e` build configuration now ALSO swaps in `app.routes.prod.ts` (on top of its existing OIDC-bypass
replacements), so it ships the same route table a production build would — verified by grepping the
built bundle for the `dev-harness` string (present with zero matches after this change, versus one match
before). A new `harness` configuration (OIDC bypass, but the ordinary `app.routes.ts` so the harness
route stays) is what `topology-harness.spec.ts` now builds/serves against instead, in its own step,
after the smoke-test server is stopped — see `angular.json`'s `build`/`serve` architect targets,
`package.json`'s `build:harness`/`serve:harness` scripts, and the `angular-e2e-smoke` job in
`.github/workflows/ci.yml`.

## Consequences

- `web/e2e/topology-harness.spec.ts` needs no seeded backend, real OIDC tenant, or `E2E_ENABLED` gate —
  it can run in every PR build, unlike `topology.smoke.spec.ts`.
- Running it for real surfaced two pre-existing, real defects, both fixed alongside this ADR: the
  ambiguous/medium-confidence badge and the confirmed/high-confidence badge fell just under WCAG AA's
  4.5:1 text contrast in the light theme (`_tokens.scss`'s `--color-status-ambiguous`/
  `--color-status-confirmed` darkened accordingly), and `topology.smoke.spec.ts`'s
  `getByRole('region').filter({ has: page.locator('h2') })` details-panel locator was ambiguous (the
  always-rendered legend is also a region with an `h2`) and would have thrown a strict-mode violation
  the first time it ever actually ran against a seeded backend.
- Follow-up (not done here): the grouped-listbox `<li role="group">` in `topology-search.component.ts`
  and Angular CDK's single global `.cdk-overlay-container` both trip axe's `aria-allowed-role`/`region`
  rules; both are tagged `best-practice` (not `wcag2a`/`wcag2aa`) and are excluded from this spec's gate
  with that reasoning inline — worth a dedicated pass if a stricter internal a11y bar is adopted later.
- The harness intentionally does not exercise real OIDC/Entra auth or the real backend/CORS surface —
  that remains covered by `CorsPolicyTests.cs`, `api_smoke`'s live-Kestrel CORS preflight checks, and
  the in-process RBAC suite. A real Entra test tenant remains a genuine external dependency this repo
  cannot self-provision.
