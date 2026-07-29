import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type {
  DriftApplyJobSummaryDto,
  DriftItemDto,
  DriftReportSummaryDto,
} from '../model/drift-contracts';
import { DriftApplyService } from '../services/drift-apply.service';
import { DriftReportService } from '../services/drift-report.service';
import { DriftReportStateService, EMPTY_DRIFT_FILTERS } from './drift-report-state.service';

function report(overrides: Partial<DriftReportSummaryDto> = {}): DriftReportSummaryDto {
  return {
    driftReportId: 'report-1',
    desiredRevisionId: 'rev-1',
    observedSnapshotId: 'snap-1',
    computedAt: '2026-01-01T00:00:00Z',
    computationVersion: 1,
    totalItems: 1,
    countsBySeverity: {},
    hasAmbiguities: false,
    isTruncated: false,
    status: 'Completed',
    errorSummary: null,
    ...overrides,
  };
}

function item(overrides: Partial<DriftItemDto> = {}): DriftItemDto {
  return {
    driftItemId: 'item-1',
    driftReportId: 'report-1',
    driftType: 'AccessVlanMismatch',
    severity: 'High',
    actionable: true,
    subjectType: 'SwitchPort',
    subjectKey: 'v1|rack|sw-01|ether5',
    expectedValue: '200',
    actualValue: '100',
    why: 'Access VLAN mismatch',
    details: null,
    createdAt: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

function job(overrides: Partial<DriftApplyJobSummaryDto> = {}): DriftApplyJobSummaryDto {
  return {
    jobId: 'job-1',
    rackId: 'rack-1',
    driftItemId: 'item-1',
    status: 'Pending',
    requestedAt: '2026-01-01T00:00:00Z',
    finishedAt: null,
    requestedBy: 'operator@example.com',
    errorCategory: null,
    errorCode: null,
    ...overrides,
  };
}

describe('DriftReportStateService', () => {
  function setup(
    opts: {
      getLatest?: ReturnType<typeof vi.fn>;
      getReportById?: ReturnType<typeof vi.fn>;
      getJobs?: ReturnType<typeof vi.fn>;
    } = {},
  ) {
    const getLatest =
      opts.getLatest ??
      vi.fn(() =>
        of({
          kind: 'ok',
          value: { report: report(), items: { items: [item()], nextCursor: null } },
        }),
      );
    const getReportById =
      opts.getReportById ??
      vi.fn(() =>
        of({
          kind: 'ok',
          value: { report: report(), items: { items: [item()], nextCursor: null } },
        }),
      );
    const getJobs =
      opts.getJobs ?? vi.fn(() => of({ kind: 'ok', value: { items: [], nextCursor: null } }));

    TestBed.configureTestingModule({
      providers: [
        {
          provide: DriftReportService,
          useValue: { getLatest, getReportById, getItemById: vi.fn() },
        },
        {
          provide: DriftApplyService,
          useValue: { getJobs, getJob: vi.fn(), applyCorrection: vi.fn() },
        },
      ],
    });

    return { service: TestBed.inject(DriftReportStateService), getLatest, getReportById, getJobs };
  }

  it('loads the latest report id then re-fetches items via getReportById with the given filters', () => {
    const { service, getLatest, getReportById } = setup();

    service.loadRackDrift('rack-1', EMPTY_DRIFT_FILTERS);

    expect(getLatest).toHaveBeenCalledWith('rack-1');
    expect(getReportById).toHaveBeenCalledWith('rack-1', 'report-1', {
      severity: undefined,
      driftType: undefined,
      actionable: undefined,
      pageSize: 50,
    });
    expect(service.report()).toEqual(report());
    expect(service.items()).toEqual([item()]);
    expect(service.loading()).toBe(false);
  });

  it('derives job status by indexing the newest job per driftItemId', () => {
    const jobsResult = of({
      kind: 'ok',
      value: {
        items: [
          job({ jobId: 'j1', status: 'Pending', requestedAt: '2026-01-01T00:00:00Z' }),
          job({ jobId: 'j2', status: 'Executing', requestedAt: '2026-01-02T00:00:00Z' }),
        ],
        nextCursor: null,
      },
    });
    const { service } = setup({ getJobs: vi.fn(() => jobsResult) });

    service.loadRackDrift('rack-1');

    expect(service.jobStatusByDriftItemId().get('item-1')).toEqual({
      jobId: 'j2',
      status: 'Executing',
    });
  });

  it('renders "—" (via the consumer) when no job exists for a drift item — covered by an empty map', () => {
    const { service } = setup();

    service.loadRackDrift('rack-1');

    expect(service.jobStatusByDriftItemId().has('item-1')).toBe(false);
  });

  it('maps a notFound getLatest result to the error signal', () => {
    const { service } = setup({ getLatest: vi.fn(() => of({ kind: 'notFound' })) });

    service.loadRackDrift('rack-1');

    expect(service.error()).toBe('notFound');
    expect(service.loading()).toBe(false);
  });

  it('setFilters re-fetches getReportById for the current report with the new filters', () => {
    const { service, getReportById } = setup();
    service.loadRackDrift('rack-1');
    getReportById.mockClear();

    service.setFilters({ severity: 'High', driftType: null, actionable: true });

    expect(getReportById).toHaveBeenCalledWith('rack-1', 'report-1', {
      severity: 'High',
      driftType: undefined,
      actionable: true,
      pageSize: 50,
    });
  });

  it('loadMore appends items and advances the cursor', () => {
    const getReportById = vi
      .fn()
      .mockReturnValueOnce(
        of({
          kind: 'ok',
          value: { report: report(), items: { items: [item()], nextCursor: 'cursor-2' } },
        }),
      )
      .mockReturnValueOnce(
        of({
          kind: 'ok',
          value: {
            report: report(),
            items: { items: [item({ driftItemId: 'item-2' })], nextCursor: null },
          },
        }),
      );
    const { service } = setup({ getReportById });
    service.loadRackDrift('rack-1');

    service.loadMore();

    expect(service.items().map((i) => i.driftItemId)).toEqual(['item-1', 'item-2']);
    expect(service.nextCursor()).toBeNull();
  });

  it('clears a prior error when switching to a rack that loads successfully', () => {
    const getLatest = vi
      .fn()
      .mockReturnValueOnce(of({ kind: 'notFound' }))
      .mockReturnValueOnce(
        of({
          kind: 'ok',
          value: { report: report(), items: { items: [item()], nextCursor: null } },
        }),
      );
    const { service } = setup({ getLatest });

    service.loadRackDrift('rack-1');
    expect(service.error()).toBe('notFound');

    service.loadRackDrift('rack-2');
    expect(service.rackId()).toBe('rack-2');
    expect(service.error()).toBeNull();
    expect(service.items()).toEqual([item()]);
  });
});
