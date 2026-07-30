import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import type { DriftApplyJobStatus } from '../model/drift-contracts';
import { JobStatusBadgeComponent } from './job-status-badge.component';

@Component({
  standalone: true,
  imports: [JobStatusBadgeComponent],
  template: `<app-job-status-badge [status]="status" />`,
})
class HostComponent {
  status: DriftApplyJobStatus = 'Pending';
}

describe('JobStatusBadgeComponent', () => {
  let fixture: ComponentFixture<HostComponent>;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [HostComponent] });
    fixture = TestBed.createComponent(HostComponent);
  });

  // Task #130: JobStatusBadgeComponent is now a thin wrapper over the shared StatusBadgeComponent, so
  // the rendered root/icon classes are `status-badge`/`status-badge__icon`, namespaced per job-status
  // bucket as `status-badge--job-{pending,success,error}` — see shared/badge/status-badge.component.ts.
  function badgeEl(): HTMLElement {
    return fixture.nativeElement.querySelector('.status-badge');
  }

  const cases: [DriftApplyJobStatus, string, string][] = [
    ['Pending', 'Pending', 'status-badge--job-pending'],
    ['Claimed', 'Claimed', 'status-badge--job-pending'],
    ['Revalidating', 'Revalidating', 'status-badge--job-pending'],
    ['Executing', 'Executing', 'status-badge--job-pending'],
    ['Completed', 'Completed', 'status-badge--job-success'],
    ['Failed', 'Failed', 'status-badge--job-error'],
    ['StaleDrift', 'Stale drift', 'status-badge--job-error'],
    ['Canceled', 'Canceled', 'status-badge--job-error'],
  ];

  it.each(cases)(
    'renders the label and css kind for status "%s"',
    (status, expectedLabel, expectedClass) => {
      fixture.componentInstance.status = status;
      fixture.detectChanges();

      expect(badgeEl().textContent).toContain(expectedLabel);
      expect(badgeEl().className).toContain(expectedClass);
    },
  );

  it('always renders an aria-hidden icon glyph alongside the text label (NFR5: never colour-only)', () => {
    fixture.componentInstance.status = 'Failed';
    fixture.detectChanges();

    const icon = badgeEl().querySelector('.status-badge__icon');
    expect(icon?.getAttribute('aria-hidden')).toBe('true');
    expect(icon?.textContent).toBeTruthy();
  });

  const inProgressIconCases: [DriftApplyJobStatus, string][] = [
    ['Pending', '…'],
    ['Claimed', '…'],
    ['Revalidating', '↻'],
    ['Executing', '↻'],
  ];

  it.each(inProgressIconCases)(
    'gives status "%s" its own glyph (%s), despite sharing the job-pending badge kind with the others',
    (status, expectedIcon) => {
      fixture.componentInstance.status = status;
      fixture.detectChanges();

      expect(badgeEl().querySelector('.status-badge__icon')?.textContent).toBe(expectedIcon);
    },
  );
});
