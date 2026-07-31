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
import { validateNetworkIntent } from '../network-config/model/network-intent-validation';
import type {
  PreflightValidationResponse,
  ValidationIssue,
} from '../network-config/model/preflight-validation-contracts';
import { DesiredStateRoundTripService } from '../network-config/services/desired-state-roundtrip.service';
import { NetworkConfigPermissionService } from '../network-config/services/network-config-permission.service';
import { NetworkIntentService } from '../network-config/services/network-intent.service';
import { PreflightValidationService } from '../network-config/services/preflight-validation.service';
import { PrService } from '../network-config/services/pr.service';
import { NetworkIntentStateService } from '../network-config/state/network-intent-state.service';
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
  harnessNetworkIntentDto,
  harnessNetworkIntentEtag,
  harnessSaveNetworkIntent,
  harnessSnapshotMeta,
  resetHarnessNetworkIntent,
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

// Story #168: mirrors DriftApplyService's fake above — the mutable harness network-intent state lives
// in fixtures.ts (resetHarnessNetworkIntent/harnessSaveNetworkIntent) so Playwright's catalogue CRUD ->
// port intent set/clear -> save -> reload-shows-same-state scenario round-trips through a real (if
// in-memory) upsert rather than always returning a fixed fixture.
const fakeNetworkIntentService: Pick<
  NetworkIntentService,
  'getIntent' | 'saveIntent' | 'validate'
> = {
  getIntent: () =>
    of({
      kind: 'ok' as const,
      value: { intent: harnessNetworkIntentDto(), etag: harnessNetworkIntentEtag() },
    }),
  saveIntent: (_rackId, request) => {
    const errors = validateNetworkIntent(request.vlanCatalogue, request.portIntents);
    if (errors.length > 0) {
      return of({
        kind: 'validationError' as const,
        errors: errors.map((e) => ({ field: e.field, messages: [e.message] })),
      });
    }
    return of({
      kind: 'ok' as const,
      value: { intent: harnessSaveNetworkIntent(request), etag: harnessNetworkIntentEtag() },
    });
  },
  validate: (_rackId, request) => {
    const errors = validateNetworkIntent(request.vlanCatalogue, request.portIntents);
    return of({
      kind: 'ok' as const,
      value: {
        isValid: errors.length === 0,
        errors: errors.map((e) => ({ field: e.field, message: e.message })),
      },
    });
  },
};

// Story #169: a wire-only fake of the server-side YAML round-trip. parse() extracts a small fixed
// supported model, captures any `extensions:`-to-EOF block byte-for-byte, and raises the comments warning
// when the pasted text contains a comment; render() deterministically re-emits the CURRENT draft (so an
// edit made between import and export is reflected) followed by the preserved extensions bytes verbatim and
// with no comments — enough for Playwright to prove the real import→edit→export UI flow end-to-end.
const fakeDesiredStateRoundTripService: Pick<DesiredStateRoundTripService, 'parse' | 'render'> = {
  parse: (_rackId, yaml) => {
    const hasComment = /(^|\s)#/m.test(yaml);
    const extIndex = yaml.indexOf('extensions:');
    const unknownBlocks =
      extIndex >= 0
        ? [
            {
              anchorPath: 'extensions',
              rawYamlText: yaml.slice(extIndex),
              checksum: 'harness-checksum',
            },
          ]
        : [];
    return of({
      kind: 'ok' as const,
      value: {
        supportedModel: {
          rackSlug: 'rack-1',
          vlanCatalogue: [{ id: 10, name: 'imported-storage', description: 'iSCSI' }],
          portIntents: [{ switchStableKey: 'sw-imported', portName: 'eth1', accessVlanId: 10 }],
        },
        unknownBlocks,
        warnings: hasComment ? ['commentsNotPreserved'] : [],
        schemaVersion: 1,
      },
    });
  },
  render: (_rackId, request) => {
    const vlans = request.vlanCatalogue
      .map((v) => `    - vlanId: ${v.id}\n      name: ${v.name}`)
      .join('\n');
    const extensions = request.unknownBlocks.map((b) => b.rawYamlText).join('');
    const yaml =
      `apiVersion: caisson.dev/v1alpha1\nkind: RackDesiredState\n` +
      `metadata:\n  rackSlug: rack-1\nspec:\n  vlans:\n${vlans}\n${extensions}`;
    return of({ kind: 'ok' as const, value: { yaml, warnings: request.warnings } });
  },
};

// Story #170: param-driven fakes of the pre-flight validation + gated PR-creation wire so Playwright can
// drive the real ValidationIssuesPanel (grouped Errors/Warnings/Safety, live regions, focus-to-first-error)
// and the real Create-PR acknowledgement dialog in a browser without a backend. `?preflight=` selects the
// scenario (default 'clean'), read at call time exactly like `?roles=`/`?discoveryStatus=` above, and every
// validate() mints a FRESH validationRunId so the shell's stale-on-edit / warning-acknowledgement gating
// behaves precisely as it does in production. A small delay() keeps the panel's "Validating…" shimmer and
// the Create-PR in-flight window observable to a real click sequence.
const HARNESS_PREFLIGHT_SCENARIOS = ['clean', 'errors', 'warnings', 'mixed'] as const;
type HarnessPreflightScenario = (typeof HARNESS_PREFLIGHT_SCENARIOS)[number];

let harnessPreflightRunCounter = 0;

function harnessPreflightResponse(rackId: string): PreflightValidationResponse {
  const raw = new URLSearchParams(window.location.search).get('preflight');
  if (raw !== null && !(HARNESS_PREFLIGHT_SCENARIOS as readonly string[]).includes(raw)) {
    throw new Error(
      `Unknown ?preflight= value "${raw}" — expected one of: ${HARNESS_PREFLIGHT_SCENARIOS.join(', ')}`,
    );
  }
  const scenario: HarnessPreflightScenario = (raw as HarnessPreflightScenario | null) ?? 'clean';

  const duplicateVlanError: ValidationIssue = {
    severity: 'error',
    code: 'semantic.vlan.duplicateId',
    message: 'VLAN ID 10 is defined more than once in this rack.',
    fieldPath: '/vlanCatalogue/1/id',
    uiPath: 'vlanCatalogue[1].id',
    entityRef: { kind: 'vlan', rackId, switchStableKey: null, portName: null, vlanId: 10 },
    helpUrl: null,
    details: null,
  };
  const descriptionWarning: ValidationIssue = {
    severity: 'warning',
    code: 'style.vlan.missingDescription',
    message: 'VLAN 20 (storage) has no description.',
    fieldPath: '/vlanCatalogue/1/description',
    uiPath: 'vlanCatalogue[1].description',
    entityRef: { kind: 'vlan', rackId, switchStableKey: null, portName: null, vlanId: 20 },
    helpUrl: null,
    details: null,
  };
  const uplinkSafetyWarning: ValidationIssue = {
    severity: 'warning',
    code: 'safety.uplinkPort',
    message: 'Port SW-1/ether1 is classified as an uplink; changing it risks isolating the rack.',
    fieldPath: '/portIntents/0/accessVlanId',
    uiPath: 'ports["SW-1|sw1/ether1"].accessVlanId',
    entityRef: {
      kind: 'port',
      rackId,
      switchStableKey: 'SW-1|sw1',
      portName: 'ether1',
      vlanId: null,
    },
    helpUrl: null,
    details: { reason: 'heuristic:lldp-uplink-neighbor' },
  };

  const errors: ValidationIssue[] =
    scenario === 'errors' || scenario === 'mixed' ? [duplicateVlanError] : [];
  const warnings: ValidationIssue[] =
    scenario === 'warnings'
      ? [uplinkSafetyWarning]
      : scenario === 'mixed'
        ? [descriptionWarning, uplinkSafetyWarning]
        : [];

  harnessPreflightRunCounter += 1;
  return {
    validationRunId: `harness-run-${harnessPreflightRunCounter}`,
    isValid: errors.length === 0,
    canCreatePr: errors.length === 0,
    errors,
    warnings,
    validatedAtUtc: harnessSnapshotMeta().createdAt,
    topologySnapshotId: 'harness-topology-snapshot',
  };
}

const fakePreflightValidationService: Pick<PreflightValidationService, 'validate'> = {
  validate: (rackId) =>
    of({ kind: 'ok' as const, value: harnessPreflightResponse(rackId) }).pipe(delay(250)),
};

const fakePrService: Pick<PrService, 'createPullRequest'> = {
  createPullRequest: (_rackId, validationRunId) =>
    of({
      kind: 'ok' as const,
      value: {
        validationRunId,
        status: 'accepted',
        detail: 'Pull request queued for creation.',
        pullRequestUrl: null,
      },
    }).pipe(delay(250)),
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
      resetNetworkIntent: typeof resetHarnessNetworkIntent;
    };
  }
}
window.__harness__ = {
  hub: fakeHub,
  bumpVersion,
  setDriftJobStatus,
  getApplyCorrectionCallCount,
  resetNetworkIntent: resetHarnessNetworkIntent,
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
  { provide: NetworkIntentService, useValue: fakeNetworkIntentService },
  { provide: DesiredStateRoundTripService, useValue: fakeDesiredStateRoundTripService },
  { provide: PreflightValidationService, useValue: fakePreflightValidationService },
  { provide: PrService, useValue: fakePrService },
  // Re-provided with useClass (not left to resolve from root) so their own inject() calls above pick up
  // the fakes registered in this same route-scoped environment injector.
  { provide: TopologyStateService, useClass: TopologyStateService },
  { provide: TopologySignalRService, useClass: TopologySignalRService },
  { provide: DriftPermissionService, useClass: DriftPermissionService },
  { provide: DriftApplyJobStatusService, useClass: DriftApplyJobStatusService },
  { provide: DriftReportStateService, useClass: DriftReportStateService },
  { provide: NetworkConfigPermissionService, useClass: NetworkConfigPermissionService },
  { provide: NetworkIntentStateService, useClass: NetworkIntentStateService },
];
