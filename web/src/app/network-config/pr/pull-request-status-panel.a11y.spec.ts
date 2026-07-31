// Automated accessibility check for the PR status panel (story #173, NFR5). color-contrast is disabled for
// the same jsdom-has-no-paint-engine reason as the other a11y specs; the real-browser contrast pass lives in
// the Playwright e2e harness. Verifies status labels, the external-link/refresh button names, and the polite
// live region carry accessible names — WCAG AA, not colour-only.
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import axe from 'axe-core';
import { describe, expect, it, vi } from 'vitest';
import { TopologySignalRService } from '../../topology/live/topology-signalr.service';
import type { PrStatusDto } from './pr-status-contracts';
import { PrStatusService } from './pr-status.service';
import { PrStatusStateService } from './pr-status-state.service';
import { PullRequestStatusPanelComponent } from './pull-request-status-panel.component';

@Component({
  standalone: true,
  imports: [PullRequestStatusPanelComponent],
  template: `<app-pull-request-status-panel [rackId]="'rack-1'" />`,
})
class HostComponent {}

const status: PrStatusDto = {
  hasPullRequest: true,
  pullRequestNumber: 42,
  pullRequestUrl: 'https://gh/pr/42',
  state: 'Open',
  headSha: 'abc123',
  checksConclusion: 'Failure',
  failingChecksCount: 1,
  checksSummary:
    '{"conclusion":"Failure","checks":[{"name":"lint","status":"completed","conclusion":"Failure"},{"name":"build","status":"completed","conclusion":"Success"}]}',
  lastUpdated: '2026-07-31T00:00:00Z',
  lastChecked: '2026-07-31T00:00:00Z',
  lastPollFailureReason: null,
  canApply: false,
  gateReasonCode: 'PrNotMerged',
};

describe('PullRequestStatusPanelComponent accessibility', () => {
  it('has no automatically-detectable accessibility violations', async () => {
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        { provide: TopologySignalRService, useValue: { trackPrStatus: vi.fn() } },
        {
          provide: PrStatusService,
          useValue: { getStatus: () => of({ kind: 'ok', value: status }) },
        },
      ],
    });
    TestBed.inject(PrStatusStateService).applyPolledStatus('rack-1', status);

    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 0));

    const results = await axe.run(fixture.nativeElement, {
      rules: { 'color-contrast': { enabled: false } },
    });

    expect(results.violations).toEqual([]);
  }, 15000);
});
