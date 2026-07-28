import type { Routes } from '@angular/router';
import { AccessDeniedComponent } from './core/access-denied/access-denied.component';
import { roleGuard } from './core/auth/role.guard';

// Rack selection/listing is out of scope for story #10 (assumed to already exist per the story's own
// assumptions); this app's only route is the topology page for an already-known rackId.
export const routes: Routes = [
  {
    path: 'racks/:rackId/topology',
    canActivate: [roleGuard],
    loadComponent: () =>
      import('./topology/topology-page.component').then((m) => m.TopologyPageComponent),
  },
  { path: 'access-denied', component: AccessDeniedComponent },
];
