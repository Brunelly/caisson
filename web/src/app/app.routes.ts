import type { Routes } from '@angular/router';
import { AccessDeniedComponent } from './core/access-denied/access-denied.component';
import { roleGuard } from './core/auth/role.guard';
import { DEV_HARNESS_PROVIDERS } from './dev-harness/dev-harness.providers';

// A statically-imported `unsavedNetworkIntentChangesGuard` would pull @angular/cdk/dialog (and
// DiscardChangesDialogComponent) into the eagerly-loaded root routes module — the SAME budget concern
// `loadComponent`'s dynamic import already solves for every route component here. Dynamically importing
// the guard module itself at the point it actually runs (only when navigating away from a vlans/ports
// route) keeps that whole dependency graph in its own lazy chunk instead. Left untyped (no
// `CanDeactivateFn<unknown>` annotation) so it infers the narrower `Promise<Observable<boolean>>` —
// see unsaved-changes.guard.ts's own comment for why the wider alias breaks this dynamic-import wrapper.
const lazyUnsavedNetworkIntentChangesGuard = () =>
  import('./network-config/shared/unsaved-changes.guard').then((m) =>
    m.unsavedNetworkIntentChangesGuard(),
  );

// Rack selection/listing is out of scope for story #10 (assumed to already exist per the story's own
// assumptions); this app's only route is the topology page for an already-known rackId.
export const routes: Routes = [
  {
    path: 'racks/:rackId/topology',
    canActivate: [roleGuard],
    loadComponent: () =>
      import('./topology/topology-page.component').then((m) => m.TopologyPageComponent),
  },
  {
    path: 'racks/:rackId/drift',
    canActivate: [roleGuard],
    loadComponent: () =>
      import('./drift/list/drift-reports-list.component').then((m) => m.DriftReportsListComponent),
  },
  {
    path: 'racks/:rackId/drift/items/:driftItemId',
    canActivate: [roleGuard],
    loadComponent: () =>
      import('./drift/detail/drift-report-details.component').then(
        (m) => m.DriftReportDetailsComponent,
      ),
  },
  {
    path: 'racks/:rackId/drift/jobs/:jobId',
    canActivate: [roleGuard],
    loadComponent: () =>
      import('./drift/audit/audit-record-view.component').then((m) => m.AuditRecordViewComponent),
  },
  // Story #168: Network Config authoring (VLAN Catalogue + Port Intent). The shell hosts the
  // tab bar/persistent Save action; vlans/ports are child routes so switching tabs never re-navigates
  // away from the shell (and its in-progress, unsaved draft) itself. canDeactivate is on the SHELL
  // route, not the vlans/ports children: Angular only re-runs a route's canDeactivate guards when that
  // route itself is deactivated, and switching between the vlans/ports children leaves the parent shell
  // route (and its component instance) active. Guarding the children instead would fire the "discard
  // unsaved changes?" dialog on every tab switch even though the shared draft in
  // NetworkIntentStateService is untouched by that navigation — AC4 only wants a warning when actually
  // leaving the authoring workspace.
  {
    path: 'racks/:rackId/network-config',
    canActivate: [roleGuard],
    canDeactivate: [lazyUnsavedNetworkIntentChangesGuard],
    loadComponent: () =>
      import('./network-config/network-config-shell.component').then(
        (m) => m.NetworkConfigShellComponent,
      ),
    children: [
      { path: '', redirectTo: 'vlans', pathMatch: 'full' },
      {
        path: 'vlans',
        loadComponent: () =>
          import('./network-config/vlan-catalogue/vlan-catalogue.component').then(
            (m) => m.VlanCatalogueComponent,
          ),
      },
      {
        path: 'ports',
        loadComponent: () =>
          import('./network-config/port-intent/port-intent.component').then(
            (m) => m.PortIntentComponent,
          ),
      },
    ],
  },
  { path: 'access-denied', component: AccessDeniedComponent },
  // Dev/CI-only route (no roleGuard, fixture data + a fake SignalR hub — see dev-harness.providers.ts):
  // lets Playwright exercise the real topology page/search/details-panel/graph components in a real
  // browser without a live OIDC tenant or backend. Swapped out entirely for the production build via
  // the same `fileReplacements` mechanism environment.ts/environment.prod.ts already use (angular.json's
  // "production" configuration replaces this whole file with app.routes.prod.ts, which omits this
  // route/import) — a runtime `environment.production` ternary here would leave the route path,
  // providers and fake-hub/fixture code reachable in the shipped bundle, since bundlers don't reliably
  // fold a property access on an imported object into a dead branch.
  {
    path: '__dev-harness__/topology/:rackId',
    providers: DEV_HARNESS_PROVIDERS,
    loadComponent: () =>
      import('./topology/topology-page.component').then((m) => m.TopologyPageComponent),
  },
  // Story #67: the same dev/CI-only harness mechanism, mirroring the production drift route nesting so
  // Playwright can exercise list -> filter -> detail -> apply -> live-status -> audit end to end (see
  // web/e2e/drift-harness.spec.ts) without a live OIDC tenant, backend, or seeded drift data.
  {
    path: '__dev-harness__/drift/:rackId',
    providers: DEV_HARNESS_PROVIDERS,
    loadComponent: () =>
      import('./drift/list/drift-reports-list.component').then((m) => m.DriftReportsListComponent),
  },
  {
    path: '__dev-harness__/drift/:rackId/items/:driftItemId',
    providers: DEV_HARNESS_PROVIDERS,
    loadComponent: () =>
      import('./drift/detail/drift-report-details.component').then(
        (m) => m.DriftReportDetailsComponent,
      ),
  },
  {
    path: '__dev-harness__/drift/:rackId/jobs/:jobId',
    providers: DEV_HARNESS_PROVIDERS,
    loadComponent: () =>
      import('./drift/audit/audit-record-view.component').then((m) => m.AuditRecordViewComponent),
  },
  // Story #168: the same dev/CI-only harness mechanism, mirroring the production network-config route
  // nesting so Playwright can exercise catalogue CRUD -> port intent set/clear -> save -> reload end to
  // end (see web/e2e/network-config-harness.spec.ts) without a live OIDC tenant or backend.
  {
    path: '__dev-harness__/network-config/:rackId',
    providers: DEV_HARNESS_PROVIDERS,
    loadComponent: () =>
      import('./network-config/network-config-shell.component').then(
        (m) => m.NetworkConfigShellComponent,
      ),
    children: [
      { path: '', redirectTo: 'vlans', pathMatch: 'full' },
      {
        path: 'vlans',
        loadComponent: () =>
          import('./network-config/vlan-catalogue/vlan-catalogue.component').then(
            (m) => m.VlanCatalogueComponent,
          ),
      },
      {
        path: 'ports',
        loadComponent: () =>
          import('./network-config/port-intent/port-intent.component').then(
            (m) => m.PortIntentComponent,
          ),
      },
    ],
  },
];
