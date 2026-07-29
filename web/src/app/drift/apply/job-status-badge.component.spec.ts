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

  function badgeEl(): HTMLElement {
    return fixture.nativeElement.querySelector('.job-status-badge');
  }

  const cases: [DriftApplyJobStatus, string, string][] = [
    ['Pending', 'Pending', 'job-status-badge--pending'],
    ['Claimed', 'Claimed', 'job-status-badge--pending'],
    ['Revalidating', 'Revalidating', 'job-status-badge--pending'],
    ['Executing', 'Executing', 'job-status-badge--pending'],
    ['Completed', 'Completed', 'job-status-badge--success'],
    ['Failed', 'Failed', 'job-status-badge--error'],
    ['StaleDrift', 'Stale drift', 'job-status-badge--error'],
    ['Canceled', 'Canceled', 'job-status-badge--error'],
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

    const icon = badgeEl().querySelector('.job-status-badge__icon');
    expect(icon?.getAttribute('aria-hidden')).toBe('true');
    expect(icon?.textContent).toBeTruthy();
  });
});
