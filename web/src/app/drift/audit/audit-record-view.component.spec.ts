import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { Subject, of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { AuditEventDto, PagedResult } from '../../topology/model/topology-contracts';
import { AuditService } from '../../topology/services/audit.service';
import type { DriftApplyJobDetailDto } from '../model/drift-contracts';
import { DriftApplyService } from '../services/drift-apply.service';
import { AuditRecordViewComponent } from './audit-record-view.component';

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
    steps: [],
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

describe('AuditRecordViewComponent', () => {
  let fixture: ComponentFixture<AuditRecordViewComponent>;
  let getJob: ReturnType<typeof vi.fn>;
  let getAudit: ReturnType<typeof vi.fn>;
  let paramMap$: Subject<ReturnType<typeof convertToParamMap>>;

  beforeEach(async () => {
    paramMap$ = new Subject();

    await TestBed.configureTestingModule({
      imports: [AuditRecordViewComponent],
      providers: [{ provide: ActivatedRoute, useValue: { paramMap: paramMap$.asObservable() } }],
    }).compileComponents();
  });

  function createWith(
    getJobImpl: ReturnType<typeof vi.fn>,
    getAuditImpl: ReturnType<typeof vi.fn>,
  ) {
    getJob = getJobImpl;
    getAudit = getAuditImpl;
    TestBed.overrideProvider(DriftApplyService, { useValue: { getJob } });
    TestBed.overrideProvider(AuditService, { useValue: { getAudit } });
    fixture = TestBed.createComponent(AuditRecordViewComponent);
  }

  it('renders actor, timestamps, correlationId, target, before/after, and outcome from DriftApplyJobDetailDto', async () => {
    createWith(
      vi.fn(() => of({ kind: 'ok', value: jobDetail() })),
      vi.fn(() => of({ kind: 'ok', value: pagedAudit([]) })),
    );
    fixture.detectChanges();
    paramMap$.next(convertToParamMap({ rackId: 'rack-1', jobId: 'job-1' }));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('operator@example.com');
    expect(text).toContain('corr-1');
    expect(text).toContain('sw-01');
    expect(text).toContain('ether5');
    expect(text).toContain('100');
    expect(text).toContain('200');
    expect(text).toContain('Completed');
  });

  it('fetches the secondary audit trail windowed by the job requested/finished timestamps and filters to this job', async () => {
    createWith(
      vi.fn(() => of({ kind: 'ok', value: jobDetail() })),
      vi.fn(() =>
        of({
          kind: 'ok',
          value: pagedAudit([
            auditEvent({ action: 'drift.apply.job.created' }),
            auditEvent({ action: 'drift.apply.job.completed', auditEventId: 'audit-2' }),
            auditEvent({ targetType: 'other-target', auditEventId: 'audit-3' }),
            auditEvent({ targetId: 'other-job', auditEventId: 'audit-4' }),
          ]),
        }),
      ),
    );
    fixture.detectChanges();
    paramMap$.next(convertToParamMap({ rackId: 'rack-1', jobId: 'job-1' }));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(getAudit).toHaveBeenCalledWith('rack-1', {
      from: '2026-01-01T00:00:00Z',
      to: '2026-01-01T00:01:00Z',
      pageSize: 200,
    });

    const items = fixture.nativeElement.querySelectorAll('.audit-view__trail li');
    expect(items.length).toBe(2);
    expect(fixture.nativeElement.textContent).toContain('drift.apply.job.created');
    expect(fixture.nativeElement.textContent).toContain('drift.apply.job.completed');
  });

  it('windows the audit query to "now" when the job has not finished yet', async () => {
    createWith(
      vi.fn(() => of({ kind: 'ok', value: jobDetail({ finishedAt: null }) })),
      vi.fn(() => of({ kind: 'ok', value: pagedAudit([]) })),
    );
    fixture.detectChanges();
    paramMap$.next(convertToParamMap({ rackId: 'rack-1', jobId: 'job-1' }));
    fixture.detectChanges();
    await fixture.whenStable();

    const call = getAudit.mock.calls[0];
    expect(call[1].from).toBe('2026-01-01T00:00:00Z');
    expect(call[1].to).not.toBeNull();
  });

  it('shows a not-found status message for a notFound result', async () => {
    createWith(
      vi.fn(() => of({ kind: 'notFound' })),
      vi.fn(() => of({ kind: 'ok', value: pagedAudit([]) })),
    );
    fixture.detectChanges();
    paramMap$.next(convertToParamMap({ rackId: 'rack-1', jobId: 'missing' }));
    fixture.detectChanges();

    const status = fixture.nativeElement.querySelector('[role="status"]');
    expect(status?.textContent).toContain('could not be found');
  });

  it('shows an alert for a generic error result', async () => {
    createWith(
      vi.fn(() => of({ kind: 'error', status: 500, correlationId: null })),
      vi.fn(() => of({ kind: 'ok', value: pagedAudit([]) })),
    );
    fixture.detectChanges();
    paramMap$.next(convertToParamMap({ rackId: 'rack-1', jobId: 'job-1' }));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[role="alert"]')).toBeTruthy();
  });

  it('renders the deviceConfirmed rollback field explicitly when false (auto-rolled-back)', async () => {
    createWith(
      vi.fn(() =>
        of({ kind: 'ok', value: jobDetail({ deviceConfirmed: false, status: 'Failed' }) }),
      ),
      vi.fn(() => of({ kind: 'ok', value: pagedAudit([]) })),
    );
    fixture.detectChanges();
    paramMap$.next(convertToParamMap({ rackId: 'rack-1', jobId: 'job-1' }));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('auto-rolled back');
  });

  it('is entirely read-only — no write-capable controls are rendered', async () => {
    createWith(
      vi.fn(() => of({ kind: 'ok', value: jobDetail() })),
      vi.fn(() => of({ kind: 'ok', value: pagedAudit([]) })),
    );
    fixture.detectChanges();
    paramMap$.next(convertToParamMap({ rackId: 'rack-1', jobId: 'job-1' }));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelectorAll('button, input, form').length).toBe(0);
  });
});
