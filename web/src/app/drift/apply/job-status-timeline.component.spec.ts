import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import type { DriftApplyJobStatus, DriftApplyStepDto } from '../model/drift-contracts';
import { JobStatusTimelineComponent } from './job-status-timeline.component';

@Component({
  standalone: true,
  imports: [JobStatusTimelineComponent],
  template: `<app-job-status-timeline [status]="status" [steps]="steps" />`,
})
class HostComponent {
  status: DriftApplyJobStatus = 'Pending';
  steps: DriftApplyStepDto[] | null = null;
}

function step(overrides: Partial<DriftApplyStepDto> = {}): DriftApplyStepDto {
  return {
    stepName: 'DeviceApply',
    status: 'Completed',
    attemptCount: 1,
    startedAt: '2026-01-01T00:00:00Z',
    finishedAt: '2026-01-01T00:00:05Z',
    durationMs: 5000,
    errorCode: null,
    errorMessage: null,
    ...overrides,
  };
}

describe('JobStatusTimelineComponent', () => {
  let fixture: ComponentFixture<HostComponent>;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [HostComponent] });
    fixture = TestBed.createComponent(HostComponent);
  });

  it('renders the generic stage ladder when no steps are supplied, highlighting the active stage', () => {
    fixture.componentInstance.status = 'Revalidating';
    fixture.detectChanges();

    const active = fixture.nativeElement.querySelector('.job-timeline__stage--active');
    expect(active?.textContent?.trim()).toBe('Revalidating');
    expect(fixture.nativeElement.querySelectorAll('.job-timeline__step').length).toBe(0);
  });

  it('marks every stage as done once the status is terminal', () => {
    fixture.componentInstance.status = 'Completed';
    fixture.detectChanges();

    const done = fixture.nativeElement.querySelectorAll('.job-timeline__stage--done');
    expect(done.length).toBe(4);
  });

  it('renders the detailed per-step list when steps are supplied', () => {
    fixture.componentInstance.status = 'Completed';
    fixture.componentInstance.steps = [
      step({ stepName: 'Revalidate' }),
      step({ stepName: 'DeviceApply' }),
    ];
    fixture.detectChanges();

    const items = fixture.nativeElement.querySelectorAll('.job-timeline__step');
    expect(items.length).toBe(2);
    expect(items[0].textContent).toContain('Revalidate');
    expect(items[1].textContent).toContain('DeviceApply');
    expect(fixture.nativeElement.querySelectorAll('.job-timeline__stage').length).toBe(0);
  });

  it('always renders the terminal job-status badge alongside the timeline', () => {
    fixture.componentInstance.status = 'Failed';
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('app-job-status-badge')).toBeTruthy();
  });
});
