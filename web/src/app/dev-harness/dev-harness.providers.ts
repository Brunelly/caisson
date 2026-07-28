// Route-scoped providers for the dev-only UI harness route (registered in app.routes.ts, only when
// `!environment.production` — see there for why). Fakes only the wire: HTTP-facing services and the
// SignalR hub connection. TopologyStateService/TopologySignalRService/TopologyPageComponent and every
// child component are the REAL production classes, re-registered here with `useClass` (not `useValue`)
// so their own `inject()` calls resolve within THIS route's environment injector and pick up the fakes
// below rather than bubbling to the root injector's real HttpClient-backed services.
//
// This exists so the search dropdown, drill-down panel, graph and live-update banner can be exercised
// by Playwright in a real browser (real layout/contrast/focus/ARIA) without a live OIDC/Entra tenant or
// backend — see web/e2e/topology-harness.spec.ts.
import type { Provider } from '@angular/core';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { of } from 'rxjs';
import {
  HUB_CONNECTION_FACTORY,
  TopologySignalRService,
} from '../topology/live/topology-signalr.service';
import { DiscoveryStatusService } from '../topology/services/discovery-status.service';
import { TopologyEntityService } from '../topology/services/topology-entity.service';
import { TopologySnapshotService } from '../topology/services/topology-snapshot.service';
import { TopologyStateService } from '../topology/state/topology-state.service';
import { FakeHubConnection } from './fake-hub-connection';
import {
  bumpVersion,
  harnessDiscoveryStatus,
  harnessEntityDetail,
  harnessGraphDto,
  harnessSnapshotMeta,
} from './fixtures';

const fakeHub = new FakeHubConnection();

const fakeSnapshotService: Pick<
  TopologySnapshotService,
  'getLatest' | 'getById' | 'getHistory' | 'getGraph' | 'getDiff'
> = {
  getLatest: () =>
    of({ kind: 'ok', value: { snapshot: harnessSnapshotMeta(), graph: harnessGraphDto() } }),
  getById: () =>
    of({ kind: 'ok', value: { snapshot: harnessSnapshotMeta(), graph: harnessGraphDto() } }),
  getHistory: () => of({ kind: 'ok', value: { items: [harnessSnapshotMeta()], nextCursor: null } }),
  getGraph: () => of({ kind: 'ok', value: harnessGraphDto() }),
  getDiff: () => of({ kind: 'notFound' }),
};

const fakeDiscoveryStatusService: Pick<DiscoveryStatusService, 'getStatus'> = {
  getStatus: () => of({ kind: 'ok', value: harnessDiscoveryStatus() }),
};

const fakeEntityService: Pick<TopologyEntityService, 'getEntity' | 'getEntityHistory'> = {
  getEntity: (_rackId: string, entityType: string, stableKey: string) =>
    of({ kind: 'ok', value: harnessEntityDetail(entityType, stableKey) }),
  getEntityHistory: (_rackId: string, entityType: string, stableKey: string) =>
    of({ kind: 'ok', value: harnessEntityDetail(entityType, stableKey).history }),
};

const fakeOidc: Pick<OidcSecurityService, 'getAccessToken'> = {
  getAccessToken: () => of('harness-fake-token'),
};

// Exposed so Playwright can drive the real SignalR reconnect/reconcile state machine and bump the
// fixture's snapshot version to simulate a live snapshot-updated event (see web/e2e/topology-harness.spec.ts).
declare global {
  interface Window {
    __harness__?: {
      hub: FakeHubConnection;
      bumpVersion: () => number;
    };
  }
}
window.__harness__ = { hub: fakeHub, bumpVersion };

export const DEV_HARNESS_PROVIDERS: Provider[] = [
  { provide: TopologySnapshotService, useValue: fakeSnapshotService },
  { provide: DiscoveryStatusService, useValue: fakeDiscoveryStatusService },
  { provide: TopologyEntityService, useValue: fakeEntityService },
  { provide: OidcSecurityService, useValue: fakeOidc },
  { provide: HUB_CONNECTION_FACTORY, useValue: () => fakeHub },
  // Re-provided with useClass (not left to resolve from root) so their own inject() calls above pick up
  // the fakes registered in this same route-scoped environment injector.
  { provide: TopologyStateService, useClass: TopologyStateService },
  { provide: TopologySignalRService, useClass: TopologySignalRService },
];
