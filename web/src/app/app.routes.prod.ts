import type { Routes } from '@angular/router';
import { AccessDeniedComponent } from './core/access-denied/access-denied.component';
import { roleGuard } from './core/auth/role.guard';

// Production build of the route table (swapped in for app.routes.ts by angular.json's "production"
// fileReplacements): identical to app.routes.ts minus the dev/CI-only harness route, so its
// fixture/fake-hub code and route path are never imported into — and never reachable in — a
// production bundle. Keep in lock-step with app.routes.ts's real routes.
export const routes: Routes = [
  {
    path: 'racks/:rackId/topology',
    canActivate: [roleGuard],
    loadComponent: () =>
      import('./topology/topology-page.component').then((m) => m.TopologyPageComponent),
  },
  { path: 'access-denied', component: AccessDeniedComponent },
];
