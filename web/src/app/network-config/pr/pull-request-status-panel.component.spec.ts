import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { TopologySignalRService } from '../../topology/live/topology-signalr.service';
import type { PrStatusDto } from './pr-status-contracts';
import { PrStatusService } from './pr-status.service';
import { PrStatusStateService } from './pr-status-state.service';
import { PullRequestStatusPanelComponent } from './pull-request-status-panel.component';

@Component({
  standalone: true,
  imports: [PullRequestStatusPanelComponent],
  template: `<app-pull-request-status-panel [rackId]="rackId" />`,
})
class HostComponent {
  rackId = 'rack-1';
}

function status(overrides: Partial<PrStatusDto> = {}): PrStatusDto {
  return {
    hasPullRequest: true,
    pullRequestNumber: 42,
    pullRequestUrl: 'https://gh/pr/42',
    state: 'Open',
    headSha: 'abc123',
    checksConclusion: 'Pending',
    failingChecksCount: null,
    checksSummary:
      '{"conclusion":"Pending","checks":[{"name":"build","status":"completed","conclusion":"Success"}]}',
    lastUpdated: '2026-07-31T00:00:00Z',
    lastChecked: new Date().toISOString(),
    lastPollFailureReason: null,
    canApply: false,
    gateReasonCode: 'PrNotMerged',
    ...overrides,
  };
}

describe('PullRequestStatusPanelComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let state: PrStatusStateService;
  let getStatus: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    getStatus = vi.fn(() => of({ kind: 'ok', value: status() }));
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        { provide: TopologySignalRService, useValue: { trackPrStatus: vi.fn() } },
        { provide: PrStatusService, useValue: { getStatus } },
      ],
    });
    state = TestBed.inject(PrStatusStateService);
  });

  function render(): void {
    fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
  }

  it('renders the PR link, number, and a not-merged gate banner', () => {
    state.applyPolledStatus('rack-1', status());
    render();

    const link = fixture.nativeElement.querySelector('.pr-panel__title-link') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('https://gh/pr/42');
    expect(link.getAttribute('target')).toBe('_blank');
    expect(fixture.nativeElement.textContent).toContain('PR #42');
    expect(fixture.nativeElement.textContent).toContain('after pull request #42 is merged');
    expect(fixture.nativeElement.querySelector('.pr-panel__gate--info')).toBeTruthy();
  });

  it('shows a success gate banner when merged', () => {
    state.applyPolledStatus(
      'rack-1',
      status({ state: 'Merged', canApply: true, gateReasonCode: 'Allowed' }),
    );
    render();

    expect(fixture.nativeElement.querySelector('.pr-panel__gate--success')).toBeTruthy();
    expect(fixture.nativeElement.textContent).toContain('Ready to apply');
  });

  it('shows the no-link representation when there is no PR', () => {
    state.applyPolledStatus(
      'rack-1',
      status({ hasPullRequest: false, pullRequestNumber: null, pullRequestUrl: null, state: null }),
    );
    render();

    expect(fixture.nativeElement.textContent).toContain('A pull request must be created first');
    expect(fixture.nativeElement.querySelector('.pr-panel__title-link')).toBeNull();
  });

  it('Refresh re-reads persisted status via the API', () => {
    state.applyPolledStatus('rack-1', status());
    render();
    getStatus.mockClear();

    const refresh = fixture.nativeElement.querySelector('.pr-panel__refresh') as HTMLButtonElement;
    refresh.click();

    expect(getStatus).toHaveBeenCalledWith('rack-1');
  });

  it('announces the current status in a polite live region', () => {
    state.applyPolledStatus('rack-1', status());
    render();

    const live = fixture.nativeElement.querySelector('[aria-live="polite"]') as HTMLElement;
    expect(live.getAttribute('role')).toBe('status');
    expect(live.textContent).toContain('#42');
  });
});
