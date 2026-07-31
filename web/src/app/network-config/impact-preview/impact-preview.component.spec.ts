// Component spec for the impact-preview screen (story #171, AC3): drives runPreview() through a fake
// DesiredStateRoundTripService (render) + fake ImpactPreviewService (preview), a real
// NetworkIntentStateService (so applyPreview/previewFresh stale-on-edit is exercised for real), and a
// spied Router. Covers grouped VLAN/port change rendering, chip filtering, topology deep links, the
// not-found (no deep link) branch, and edit-invalidates-preview. Mirrors port-intent.component.spec.ts's
// signal-backed-stub + real-state conventions.
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { ToastService } from '../../shared/toast/toast.service';
import type { ApiResult } from '../../topology/services/api-result';
import type { ImpactChange, ImpactPreviewResponse } from '../model/impact-preview-contracts';
import type { NetworkIntentEnvelope } from '../services/network-intent.service';
import { NetworkIntentService } from '../services/network-intent.service';
import type { DesiredStateRenderResult } from '../services/desired-state-roundtrip.service';
import { DesiredStateRoundTripService } from '../services/desired-state-roundtrip.service';
import type { ImpactPreviewResult } from '../services/impact-preview.service';
import { ImpactPreviewService } from '../services/impact-preview.service';
import { NetworkIntentStateService } from '../state/network-intent-state.service';
import { ImpactPreviewComponent } from './impact-preview.component';

const rackId = 'rack-1';

function change(overrides: Partial<ImpactChange>): ImpactChange {
  return {
    kind: 'Modified',
    category: 'Vlan',
    changeId: 'c',
    summary: 'a change',
    entityRef: { kind: 'vlan', rackId, switchStableKey: null, portName: null, vlanId: 1 },
    existsInTopology: true,
    before: [],
    after: [],
    ...overrides,
  };
}

function previewResponse(): ImpactPreviewResponse {
  return {
    candidateId: 'cand-1',
    candidateSha256: 'sha-candidate',
    baselineSha256: 'sha-baseline',
    baselineRevisionId: 'rev-1',
    baselineCommitSha: 'commit-1',
    cacheHit: false,
    createdAtUtc: '2026-07-31T00:00:00Z',
    rawUnifiedDiff: '@@ -1,1 +1,1 @@\n-old\n+new\n',
    vlanChanges: [
      change({
        changeId: 'v-add',
        kind: 'Added',
        summary: 'VLAN 30 added',
        entityRef: { kind: 'vlan', rackId, switchStableKey: null, portName: null, vlanId: 30 },
        existsInTopology: true,
      }),
      change({
        changeId: 'v-rem',
        kind: 'Removed',
        summary: 'VLAN 40 removed',
        entityRef: { kind: 'vlan', rackId, switchStableKey: null, portName: null, vlanId: 40 },
        existsInTopology: true,
      }),
      change({
        changeId: 'v-mod',
        kind: 'Modified',
        summary: 'VLAN 50 changed',
        entityRef: { kind: 'vlan', rackId, switchStableKey: null, portName: null, vlanId: 50 },
        existsInTopology: false,
      }),
    ],
    portChanges: [
      change({
        changeId: 'p-mod',
        kind: 'Modified',
        category: 'Port',
        summary: 'ether1 re-assigned',
        entityRef: {
          kind: 'port',
          rackId,
          switchStableKey: 'SW-1',
          portName: 'ether1',
          vlanId: null,
        },
        existsInTopology: true,
      }),
    ],
  };
}

function loadedEnvelope(): ApiResult<NetworkIntentEnvelope> {
  return {
    kind: 'ok',
    value: {
      intent: { rackId, vlanCatalogue: [], portIntents: [], updatedAtUtc: null, updatedBy: null },
      etag: null,
    },
  };
}

describe('ImpactPreviewComponent', () => {
  let navigate: ReturnType<typeof vi.fn>;
  let state: NetworkIntentStateService;

  function setup(response: ImpactPreviewResponse = previewResponse()) {
    navigate = vi.fn().mockResolvedValue(true);

    const roundTrip = {
      render: () =>
        of<DesiredStateRenderResult>({
          kind: 'ok',
          value: { yaml: 'apiVersion: caisson.dev/v1alpha1\n', warnings: [] },
        }),
    } satisfies Pick<DesiredStateRoundTripService, 'render'>;

    const impact = {
      preview: () => of<ImpactPreviewResult>({ kind: 'ok', value: response }),
    } satisfies Pick<ImpactPreviewService, 'preview'>;

    const toast = { success: vi.fn(), error: vi.fn() } satisfies Pick<
      ToastService,
      'success' | 'error'
    >;

    const networkIntent = {
      getIntent: () => of(loadedEnvelope()),
    } satisfies Pick<NetworkIntentService, 'getIntent'>;

    TestBed.configureTestingModule({
      imports: [ImpactPreviewComponent],
      providers: [
        { provide: NetworkIntentService, useValue: networkIntent },
        { provide: DesiredStateRoundTripService, useValue: roundTrip },
        { provide: ImpactPreviewService, useValue: impact },
        { provide: ToastService, useValue: toast },
        { provide: Router, useValue: { navigate } },
      ],
    });

    state = TestBed.inject(NetworkIntentStateService);
    state.load(rackId);

    const fixture = TestBed.createComponent(ImpactPreviewComponent);
    fixture.detectChanges();
    return fixture;
  }

  async function runPreview(fixture: ReturnType<typeof setup>) {
    const runButton = fixture.nativeElement.querySelector(
      '.impact-preview__run',
    ) as HTMLButtonElement;
    runButton.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function chips(fixture: ReturnType<typeof setup>): HTMLButtonElement[] {
    return Array.from(fixture.nativeElement.querySelectorAll('.impact-preview__chip'));
  }

  function rows(fixture: ReturnType<typeof setup>): HTMLElement[] {
    return Array.from(fixture.nativeElement.querySelectorAll('.impact-preview__change'));
  }

  it('renders the grouped VLAN and port changes with impact chips', async () => {
    const fixture = setup();
    await runPreview(fixture);

    // One chip per group bucket (vlan added/removed/modified + port).
    expect(chips(fixture)).toHaveLength(4);

    // Both vlanChanges and portChanges are rendered together in the "all" view.
    const text = rows(fixture)
      .map((r) => r.textContent ?? '')
      .join(' | ');
    expect(rows(fixture)).toHaveLength(4);
    expect(text).toContain('VLAN 30 added');
    expect(text).toContain('ether1 re-assigned');
  });

  it('filters the change list when a chip is toggled', async () => {
    const fixture = setup();
    await runPreview(fixture);

    // Chip 0 is "VLANs added" — activating it narrows to the single Added VLAN change.
    chips(fixture)[0].click();
    fixture.detectChanges();

    expect(rows(fixture)).toHaveLength(1);
    expect(rows(fixture)[0].textContent).toContain('VLAN 30 added');
  });

  it('deep-links a VLAN change to the topology with focus "vlan:<id>"', async () => {
    const fixture = setup();
    await runPreview(fixture);

    // Isolate the added VLAN row (existsInTopology === true), then follow its deep link.
    chips(fixture)[0].click();
    fixture.detectChanges();
    const deeplink = rows(fixture)[0].querySelector(
      '.impact-preview__deeplink',
    ) as HTMLButtonElement;
    deeplink.click();

    expect(navigate).toHaveBeenCalledWith(['/racks', rackId, 'topology'], {
      queryParams: { focus: 'vlan:30' },
    });
  });

  it('deep-links a port change to the topology with focus "port:<sw>/<port>"', async () => {
    const fixture = setup();
    await runPreview(fixture);

    // Chip 3 is the ports bucket — the single port change exists in topology.
    chips(fixture)[3].click();
    fixture.detectChanges();
    const deeplink = rows(fixture)[0].querySelector(
      '.impact-preview__deeplink',
    ) as HTMLButtonElement;
    deeplink.click();

    expect(navigate).toHaveBeenCalledWith(['/racks', rackId, 'topology'], {
      queryParams: { focus: 'port:SW-1/ether1' },
    });
  });

  it('renders a not-found badge (and no deep link) when existsInTopology is false', async () => {
    const fixture = setup();
    await runPreview(fixture);

    // Chip 2 is the modified-VLANs bucket — its single change is not found in topology.
    chips(fixture)[2].click();
    fixture.detectChanges();

    const row = rows(fixture)[0];
    expect(row.querySelector('.impact-preview__notfound')).toBeTruthy();
    expect(row.querySelector('.impact-preview__deeplink')).toBeNull();
  });

  it('marks the preview fresh on success and invalidates it on the next draft edit', async () => {
    const fixture = setup();
    await runPreview(fixture);

    expect(state.previewFresh()).toBe(true);
    expect(state.previewCandidateId()).toBe('cand-1');

    // Any draft mutation (stale-on-edit, AC3) invalidates the just-computed preview.
    state.addVlan({ id: 99, name: 'new', description: null });

    expect(state.previewFresh()).toBe(false);
    expect(state.previewCandidateId()).toBeNull();
  });
});
