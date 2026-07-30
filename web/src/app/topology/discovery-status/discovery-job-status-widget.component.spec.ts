import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import type { DiscoveryStatusDto } from '../model/topology-contracts';
import { DiscoveryJobStatusWidgetComponent } from './discovery-job-status-widget.component';

@Component({
  standalone: true,
  imports: [DiscoveryJobStatusWidgetComponent],
  template: `<app-discovery-job-status-widget [status]="status" />`,
})
class HostComponent {
  status: DiscoveryStatusDto | null = null;
}

function baseStatus(overrides: Partial<DiscoveryStatusDto> = {}): DiscoveryStatusDto {
  return {
    rackId: 'rack-1',
    latestJob: {
      jobId: 'job-8f2c41d9a7',
      rackId: 'rack-1',
      mode: 'Scheduled',
      status: 'Succeeded',
      createdAt: '2026-01-01T12:00:00Z',
      startedAt: '2026-01-01T12:00:00Z',
      finishedAt: '2026-01-01T12:02:00Z',
      triggeredBy: 'scheduler',
      dryRun: false,
      errorCode: null,
      lastSuccessAt: '2026-01-01T12:02:00Z',
    },
    lastSuccessAt: '2026-01-01T12:02:00Z',
    scheduleEnabled: true,
    nextRunAt: '2026-01-01T13:00:00Z',
    ...overrides,
  };
}

describe('DiscoveryJobStatusWidgetComponent', () => {
  let fixture: ComponentFixture<HostComponent>;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [HostComponent] });
    fixture = TestBed.createComponent(HostComponent);
  });

  function widgetEl(): HTMLElement | null {
    return fixture.nativeElement.querySelector('.djs-widget');
  }

  function badgeEl(): HTMLElement | null {
    return fixture.nativeElement.querySelector('.status-badge');
  }

  it('renders nothing when status is null (loading/error, no data yet)', () => {
    fixture.componentInstance.status = null;
    fixture.detectChanges();

    expect(widgetEl()).toBeNull();
  });

  // Caisson.Domain.Enums.DiscoveryJobStatus -> JobStatusBadgeKind, mirroring
  // drift/apply/job-status-badge.component.spec.ts's per-status coverage.
  const cases: [string, string][] = [
    ['Queued', 'status-badge--job-pending'],
    ['InProgress', 'status-badge--job-pending'],
    ['Succeeded', 'status-badge--job-success'],
    ['Failed', 'status-badge--job-error'],
    ['Canceled', 'status-badge--job-error'],
  ];

  it.each(cases)('maps DiscoveryJobStatus "%s" onto badge kind %s', (status, expectedClass) => {
    fixture.componentInstance.status = baseStatus({
      latestJob: { ...baseStatus().latestJob!, status },
    });
    fixture.detectChanges();

    const badge = badgeEl();
    expect(badge?.className).toContain(expectedClass);
    expect(badge?.textContent).toContain(status);
  });

  it('handles latestJob: null (no discovery job has ever run for this rack)', () => {
    fixture.componentInstance.status = baseStatus({ latestJob: null });
    fixture.detectChanges();

    expect(badgeEl()).toBeNull();
    expect(fixture.nativeElement.querySelector('.djs-widget__none')?.textContent).toContain(
      'No discovery job yet',
    );
  });

  it('renders the job id in monospace and the errorCode when present', () => {
    fixture.componentInstance.status = baseStatus({
      latestJob: { ...baseStatus().latestJob!, status: 'Failed', errorCode: 'SnmpTimeout' },
    });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.djs-widget__job-id')?.textContent).toContain(
      'job-8f2c41d9a7',
    );
    expect(fixture.nativeElement.querySelector('.djs-widget__error')?.textContent).toContain(
      'SnmpTimeout',
    );
  });

  it('renders lastSuccessAt and, only when scheduleEnabled, nextRunAt', () => {
    fixture.componentInstance.status = baseStatus();
    fixture.detectChanges();

    const meta = Array.from(fixture.nativeElement.querySelectorAll('.djs-widget__meta')).map(
      (el) => (el as HTMLElement).textContent,
    );
    expect(meta.some((t) => t?.includes('Last success'))).toBe(true);
    expect(meta.some((t) => t?.includes('Next run'))).toBe(true);
  });

  it('does not render a next-run meta line when scheduleEnabled is false, even if nextRunAt is set', () => {
    fixture.componentInstance.status = baseStatus({ scheduleEnabled: false });
    fixture.detectChanges();

    const meta = Array.from(fixture.nativeElement.querySelectorAll('.djs-widget__meta')).map(
      (el) => (el as HTMLElement).textContent,
    );
    expect(meta.some((t) => t?.includes('Next run'))).toBe(false);
  });

  // AC1/NFR3: no invented metrics/nav — this pins down that the widget's rendered text never grows a
  // device-progress counter, elapsed/retry timer, or "Job log" link, none of which exist on the DTO.
  it('renders no field beyond DiscoveryStatusDto/DiscoveryJobSummaryDto (no invented progress/timer/log-link)', () => {
    fixture.componentInstance.status = baseStatus();
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).not.toMatch(/devices?/i);
    expect(text).not.toMatch(/elapsed/i);
    expect(text).not.toMatch(/retry/i);
    expect(fixture.nativeElement.querySelector('a')).toBeNull();
  });
});
