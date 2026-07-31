// Accessibility check for the impact-preview screen (story #171, NFR5): renders a ready preview (grouped
// change list + diff viewer) and asserts no axe violations across the light/dark/hc-dark themes. The
// round-trip + impact services and page state are stubbed so the "ready" state renders without any HTTP;
// color-contrast is disabled (jsdom has no paint engine — real-browser contrast lives in the e2e harness).
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import axe from 'axe-core';
import { of } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ToastService } from '../../shared/toast/toast.service';
import type { ApiResult } from '../../topology/services/api-result';
import type { ImpactPreviewResponse } from '../model/impact-preview-contracts';
import type { NetworkIntentEnvelope } from '../services/network-intent.service';
import { NetworkIntentService } from '../services/network-intent.service';
import type { DesiredStateRenderResult } from '../services/desired-state-roundtrip.service';
import { DesiredStateRoundTripService } from '../services/desired-state-roundtrip.service';
import type { ImpactPreviewResult } from '../services/impact-preview.service';
import { ImpactPreviewService } from '../services/impact-preview.service';
import { NetworkIntentStateService } from '../state/network-intent-state.service';
import { ImpactPreviewComponent } from './impact-preview.component';

const rackId = 'rack-1';

function previewResponse(): ImpactPreviewResponse {
  return {
    candidateId: 'cand-1',
    candidateSha256: 'sha-candidate',
    baselineSha256: 'sha-baseline',
    baselineRevisionId: 'rev-1',
    baselineCommitSha: 'commit-1',
    cacheHit: false,
    createdAtUtc: '2026-07-31T00:00:00Z',
    rawUnifiedDiff: '@@ -1,2 +1,2 @@\n context\n-old\n+new\n',
    vlanChanges: [
      {
        kind: 'Added',
        category: 'Vlan',
        changeId: 'v-add',
        summary: 'VLAN 30 added',
        entityRef: { kind: 'vlan', rackId, switchStableKey: null, portName: null, vlanId: 30 },
        existsInTopology: true,
        before: [],
        after: [],
      },
    ],
    portChanges: [
      {
        kind: 'Modified',
        category: 'Port',
        changeId: 'p-mod',
        summary: 'ether1 re-assigned',
        entityRef: {
          kind: 'port',
          rackId,
          switchStableKey: 'SW-1',
          portName: 'ether1',
          vlanId: null,
        },
        existsInTopology: false,
        before: [],
        after: [],
      },
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

describe('ImpactPreviewComponent accessibility', () => {
  afterEach(() => {
    document.documentElement.removeAttribute('data-theme');
    document.body.querySelectorAll('.impact-preview').forEach((n) => n.remove());
  });

  it('has no axe violations with a ready preview across light/dark/hc-dark', async () => {
    const roundTrip = {
      render: () =>
        of<DesiredStateRenderResult>({
          kind: 'ok',
          value: { yaml: 'apiVersion: caisson.dev/v1alpha1\n', warnings: [] },
        }),
    } satisfies Pick<DesiredStateRoundTripService, 'render'>;

    const impact = {
      preview: () => of<ImpactPreviewResult>({ kind: 'ok', value: previewResponse() }),
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
        { provide: Router, useValue: { navigate: vi.fn().mockResolvedValue(true) } },
      ],
    });

    TestBed.inject(NetworkIntentStateService).load(rackId);

    const fixture = TestBed.createComponent(ImpactPreviewComponent);
    document.body.appendChild(fixture.nativeElement);
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('.impact-preview__run') as HTMLButtonElement).click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('.impact-preview__changes')).toBeTruthy();

    for (const theme of ['light', 'dark', 'hc-dark'] as const) {
      document.documentElement.setAttribute('data-theme', theme);
      fixture.detectChanges();
      await fixture.whenStable();
      const results = await axe.run(host, { rules: { 'color-contrast': { enabled: false } } });
      expect(results.violations, `axe violations under the ${theme} theme`).toEqual([]);
    }
  }, 15000);
});
