// Automated accessibility check for the audit/job-record view (NFR5), including the newly-badge-ified
// per-step job status (job-status-timeline.component.ts) and the audit-trail `event.result` badge
// (Task #130). `color-contrast` is disabled for the same jsdom-has-no-paint-engine reason as
// topology-page.a11y.spec.ts; the real-browser contrast pass lives in web/e2e/drift-harness.spec.ts.
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import axe from 'axe-core';
import { Subject, of } from 'rxjs';
import { describe, expect, it } from 'vitest';
import type { AuditEventDto, PagedResult } from '../../topology/model/topology-contracts';
import { AuditService } from '../../topology/services/audit.service';
import type { DriftApplyJobDetailDto, DriftApplyStepDto } from '../model/drift-contracts';
import { DriftApplyService } from '../services/drift-apply.service';
import { AuditRecordViewComponent } from './audit-record-view.component';

function step(overrides: Partial<DriftApplyStepDto> = {}): DriftApplyStepDto {
  return {
    stepName: 'DeviceApply',
    status: 'Succeeded',
    attemptCount: 1,
    startedAt: '2026-01-01T00:00:00Z',
    finishedAt: '2026-01-01T00:00:05Z',
    durationMs: 5000,
    errorCode: null,
    errorMessage: null,
    ...overrides,
  };
}

function jobDetail(overrides: Partial<DriftApplyJobDetailDto> = {}): DriftApplyJobDetailDto {
  return {
    jobId: 'job-1',
    rackId: 'rack-1',
    driftItemId: 'item-1',
    status: 'Completed',
    requestedAt: '2026-01-01T00:00:00Z',
    claimedAt: '2026-01-01T00:00:01Z',
    finishedAt: '2026-01-01T00:01:00Z',
    requestedBy: 'operator@example.com',
    actorType: 'User',
    correlationId: 'corr-1',
    attemptCount: 1,
    currentStep: null,
    switchDeviceKey: 'sw-01',
    portName: 'ether5',
    desiredVlanId: 200,
    deviceReasonCode: null,
    deviceConfirmed: true,
    beforeState: '100',
    afterState: '200',
    errorCategory: null,
    errorCode: null,
    errorMessage: null,
    steps: [step({ stepName: 'Revalidate' }), step({ stepName: 'DeviceApply', status: 'Failed' })],
    ...overrides,
  };
}

function auditEvent(overrides: Partial<AuditEventDto> = {}): AuditEventDto {
  return {
    auditEventId: 'audit-1',
    rackId: 'rack-1',
    snapshotId: null,
    occurredAt: '2026-01-01T00:00:00Z',
    actorType: 'User',
    actorId: 'operator@example.com',
    action: 'drift.apply.job.created',
    targetType: 'drift-apply-job',
    targetId: 'job-1',
    result: 'Created',
    correlationId: 'corr-1',
    ...overrides,
  };
}

function pagedAudit(items: AuditEventDto[]): PagedResult<AuditEventDto> {
  return { items, nextCursor: null };
}

async function render(): Promise<ComponentFixture<AuditRecordViewComponent>> {
  const paramMap$ = new Subject<ReturnType<typeof convertToParamMap>>();

  await TestBed.configureTestingModule({
    imports: [AuditRecordViewComponent],
    providers: [
      { provide: ActivatedRoute, useValue: { paramMap: paramMap$.asObservable() } },
      {
        provide: DriftApplyService,
        useValue: { getJob: () => of({ kind: 'ok', value: jobDetail() }) },
      },
      {
        provide: AuditService,
        useValue: {
          getAudit: () =>
            of({
              kind: 'ok',
              value: pagedAudit([
                auditEvent({ result: 'Created' }),
                auditEvent({
                  auditEventId: 'audit-2',
                  action: 'drift.apply.job.read',
                  result: 'success',
                }),
              ]),
            }),
        },
      },
    ],
  }).compileComponents();

  const fixture = TestBed.createComponent(AuditRecordViewComponent);
  fixture.detectChanges();
  paramMap$.next(convertToParamMap({ rackId: 'rack-1', jobId: 'job-1' }));
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return fixture;
}

describe('AuditRecordViewComponent accessibility', () => {
  it('renders the per-step job-status badges and the audit-trail result badges (Task #130)', async () => {
    const fixture = await render();

    expect(fixture.nativeElement.querySelectorAll('.job-timeline__step .status-badge').length).toBe(
      2,
    );
    expect(fixture.nativeElement.querySelectorAll('.audit-view__trail .status-badge').length).toBe(
      2,
    );
  });

  it('has no automatically-detectable accessibility violations', async () => {
    const fixture = await render();

    const results = await axe.run(fixture.nativeElement, {
      rules: { 'color-contrast': { enabled: false } },
    });

    expect(results.violations).toEqual([]);
  }, 15000);
});
