import type { Routes } from '@angular/router';
import { AccessDeniedComponent } from './core/access-denied/access-denied.component';
import { roleGuard } from './core/auth/role.guard';
import { DEV_HARNESS_PROVIDERS } from './dev-harness/dev-harness.providers';

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
];
