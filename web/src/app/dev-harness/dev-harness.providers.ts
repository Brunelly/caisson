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
import { delay, of } from 'rxjs';
import { DriftPermissionService } from '../core/auth/drift-permission.service';
import { DriftApplyJobStatusService } from '../drift/live/drift-apply-job-status.service';
import { DriftApplyService } from '../drift/services/drift-apply.service';
import type { DriftReportItemFilters } from '../drift/services/drift-report.service';
import { DriftReportService } from '../drift/services/drift-report.service';
import { DriftReportStateService } from '../drift/state/drift-report-state.service';
import {
  HUB_CONNECTION_FACTORY,
  TopologySignalRService,
} from '../topology/live/topology-signalr.service';
import { AuditService } from '../topology/services/audit.service';
import { DiscoveryStatusService } from '../topology/services/discovery-status.service';
import { TopologyEntityService } from '../topology/services/topology-entity.service';
import { TopologySnapshotService } from '../topology/services/topology-snapshot.service';
import { TopologyStateService } from '../topology/state/topology-state.service';
import { FakeHubConnection } from './fake-hub-connection';
import {
  HARNESS_DISCOVERY_STATUS_VARIANTS,
  HARNESS_DRIFT_JOB_ID,
  bumpVersion,
  currentDriftJobStatus,
  harnessDiscoveryStatus,
  harnessDriftApplyJobDetail,
  harnessDriftApplyJobSummary,
  harnessDriftItem,
  harnessDriftReportDetail,
  harnessDriftReportSummary,
  harnessEntityDetail,
  harnessGraphDto,
  harnessSnapshotMeta,
  setDriftJobStatus,
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

// Task #135: `?discoveryStatus=` selects the DiscoveryJobStatusWidgetComponent fixture variant, read at
// call time the same way `fakeOidc`'s `?roles=` param is below — 'succeeded' is the unparameterised
// default, matching every existing test's expectations.
const fakeDiscoveryStatusService: Pick<DiscoveryStatusService, 'getStatus'> = {
  getStatus: () => {
    const raw = new URLSearchParams(window.location.search).get('discoveryStatus');
    if (raw !== null && !(HARNESS_DISCOVERY_STATUS_VARIANTS as readonly string[]).includes(raw)) {
      throw new Error(
        `Unknown ?discoveryStatus= value "${raw}" — expected one of: ${HARNESS_DISCOVERY_STATUS_VARIANTS.join(', ')}`,
      );
    }
    const variant = raw as (typeof HARNESS_DISCOVERY_STATUS_VARIANTS)[number] | null;
    return of({ kind: 'ok', value: harnessDiscoveryStatus(variant ?? undefined) });
  },
};

const fakeEntityService: Pick<TopologyEntityService, 'getEntity' | 'getEntityHistory'> = {
  getEntity: (_rackId: string, entityType: string, stableKey: string) =>
    of({ kind: 'ok', value: harnessEntityDetail(entityType, stableKey) }),
  getEntityHistory: (_rackId: string, entityType: string, stableKey: string) =>
    of({ kind: 'ok', value: harnessEntityDetail(entityType, stableKey).history }),
};

// Story #67: the roles claim is parameterised via a `?roles=` query param on the harness URL (read at
// call time, so a fresh page.goto() per Playwright test picks up whatever the test navigated to) —
// RBAC-hidden (no DriftApply) is the harness DEFAULT so the common case needs no query param at all;
// a test exercising the permission-present path navigates to `...?roles=ReadOnly,DriftApply`.
const fakeOidc: Pick<OidcSecurityService, 'getAccessToken' | 'getPayloadFromAccessToken'> = {
  getAccessToken: () => of('harness-fake-token'),
  getPayloadFromAccessToken: () => {
    const rolesParam = new URLSearchParams(window.location.search).get('roles');
    const roles = rolesParam ? rolesParam.split(',') : ['ReadOnly'];
    return of({ roles });
  },
};

const fakeDriftReportService: Pick<
  DriftReportService,
  'getLatest' | 'getReportById' | 'getItemById'
> = {
  getLatest: () => of({ kind: 'ok', value: harnessDriftReportDetail() }),
  getReportById: (_rackId: string, _reportId: string, filters: DriftReportItemFilters = {}) => {
    let items = [harnessDriftItem()];
    if (filters.severity) {
      items = items.filter((i) => i.severity === filters.severity);
    }
    if (filters.driftType) {
      items = items.filter((i) => i.driftType === filters.driftType);
    }
    if (filters.actionable !== undefined) {
      items = items.filter((i) => i.actionable === filters.actionable);
    }
    return of({
      kind: 'ok',
      value: { report: harnessDriftReportSummary(), items: { items, nextCursor: null } },
    });
  },
  getItemById: () => of({ kind: 'ok', value: harnessDriftItem() }),
};

// Story #67: a small artificial delay so a real double-click in Playwright has an observable in-flight
// window (a genuinely synchronous fake would resolve before a second click event could ever dispatch,
// making the double-submit-guard E2E scenario untestable) — see web/e2e/drift-harness.spec.ts. The call
// count is exposed via window.__harness__ so that scenario can assert exactly one call fired.
let applyCorrectionCallCount = 0;
export function getApplyCorrectionCallCount(): number {
  return applyCorrectionCallCount;
}

const fakeDriftApplyService: Pick<DriftApplyService, 'applyCorrection' | 'getJob' | 'getJobs'> = {
  applyCorrection: () => {
    applyCorrectionCallCount += 1;
    setDriftJobStatus('Pending');
    return of({ kind: 'created' as const, jobId: HARNESS_DRIFT_JOB_ID }).pipe(delay(400));
  },
  getJob: (_rackId: string, jobId: string) =>
    of({ kind: 'ok', value: harnessDriftApplyJobDetail({ jobId }) }),
  getJobs: () => {
    const status = currentDriftJobStatus();
    return of({
      kind: 'ok',
      value: { items: status ? [harnessDriftApplyJobSummary()] : [], nextCursor: null },
    });
  },
};

const fakeAuditService: Pick<AuditService, 'getAudit'> = {
  getAudit: () =>
    of({
      kind: 'ok',
      value: {
        items: [
          {
            auditEventId: 'harness-audit-1',
            rackId: 'rack-1',
            snapshotId: null,
            occurredAt: harnessSnapshotMeta().createdAt,
            actorType: 'User',
            actorId: 'harness-user',
            action: 'drift.apply.job.created',
            targetType: 'drift-apply-job',
            targetId: HARNESS_DRIFT_JOB_ID,
            result: 'Created',
            correlationId: 'harness-correlation-drift-1',
          },
        ],
        nextCursor: null,
      },
    }),
};

// Exposed so Playwright can drive the real SignalR reconnect/reconcile state machine, bump the
// fixture's snapshot version to simulate a live snapshot-updated event, and (story #67) drive the
// drift-apply job status forward — see web/e2e/topology-harness.spec.ts and web/e2e/drift-harness.spec.ts.
declare global {
  interface Window {
    __harness__?: {
      hub: FakeHubConnection;
      bumpVersion: () => number;
      setDriftJobStatus: typeof setDriftJobStatus;
      getApplyCorrectionCallCount: typeof getApplyCorrectionCallCount;
    };
  }
}
window.__harness__ = {
  hub: fakeHub,
  bumpVersion,
  setDriftJobStatus,
  getApplyCorrectionCallCount,
};

export const DEV_HARNESS_PROVIDERS: Provider[] = [
  { provide: TopologySnapshotService, useValue: fakeSnapshotService },
  { provide: DiscoveryStatusService, useValue: fakeDiscoveryStatusService },
  { provide: TopologyEntityService, useValue: fakeEntityService },
  { provide: OidcSecurityService, useValue: fakeOidc },
  { provide: HUB_CONNECTION_FACTORY, useValue: () => fakeHub },
  { provide: DriftReportService, useValue: fakeDriftReportService },
  { provide: DriftApplyService, useValue: fakeDriftApplyService },
  { provide: AuditService, useValue: fakeAuditService },
  // Re-provided with useClass (not left to resolve from root) so their own inject() calls above pick up
  // the fakes registered in this same route-scoped environment injector.
  { provide: TopologyStateService, useClass: TopologyStateService },
  { provide: TopologySignalRService, useClass: TopologySignalRService },
  { provide: DriftPermissionService, useClass: DriftPermissionService },
  { provide: DriftApplyJobStatusService, useClass: DriftApplyJobStatusService },
  { provide: DriftReportStateService, useClass: DriftReportStateService },
];
