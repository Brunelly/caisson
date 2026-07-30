import type { Routes } from '@angular/router';
import { AccessDeniedComponent } from './core/access-denied/access-denied.component';
import { roleGuard } from './core/auth/role.guard';

// See app.routes.ts's identical helper for the rationale: a dynamic import so @angular/cdk/dialog (and
// DiscardChangesDialogComponent) stay in their own lazy chunk rather than the eagerly-loaded root
// routes module. Left untyped (no `CanDeactivateFn<unknown>` annotation) so it infers the narrower
// `Promise<Observable<boolean>>` — see unsaved-changes.guard.ts's own comment for why the wider alias
// breaks this dynamic-import wrapper.
const lazyUnsavedNetworkIntentChangesGuard = () =>
  import('./network-config/shared/unsaved-changes.guard').then((m) =>
    m.unsavedNetworkIntentChangesGuard(),
  );

// Production build of the route table (swapped in for app.routes.ts by angular.json's "production"
// fileReplacements): identical to app.routes.ts minus the dev/CI-only harness route, so its
// fixture/fake-hub code and route path are never imported into — and never reachable in — a
// production bundle. Keep in lock-step with app.routes.ts's real routes.
export const routes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    canActivate: [roleGuard],
    loadComponent: () =>
      import('./shell/rack-landing/rack-landing.component').then((m) => m.RackLandingComponent),
  },
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
  // Story #168: Network Config authoring (VLAN Catalogue + Port Intent) — keep in lock-step with the
  // real routes in app.routes.ts, including canDeactivate living on the shell route (see that file's
  // comment): switching between the vlans/ports children must not trigger the discard-changes dialog.
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
];
