// Automated accessibility check for the drift reports list (NFR5). Runs axe-core against a
// fully-rendered fixture (populated table + severity/drift-type/actionable filters) under vitest/jsdom.
// `color-contrast` is disabled here for the same reason topology-page.a11y.spec.ts disables it — jsdom
// has no real layout/paint engine; the real-browser contrast check lives in
// web/e2e/drift-harness.spec.ts (@axe-core/playwright).
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router } from '@angular/router';
import axe from 'axe-core';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import type { DriftItemDto } from '../model/drift-contracts';
import { DriftReportStateService, EMPTY_DRIFT_FILTERS } from '../state/drift-report-state.service';
import { DriftReportsListComponent } from './drift-reports-list.component';

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

describe('DriftReportsListComponent accessibility', () => {
  let fixture: ComponentFixture<DriftReportsListComponent>;

  beforeEach(async () => {
    const stateStub = {
      rackId: () => 'rack-1',
      report: () => null,
      items: () => [
        item({ severity: 'High' }),
        item({ driftItemId: 'item-2', severity: 'Medium' }),
      ],
      nextCursor: () => null,
      filters: () => EMPTY_DRIFT_FILTERS,
      jobStatusByDriftItemId: () => new Map([['item-1', { jobId: 'job-1', status: 'Executing' }]]),
      loading: () => false,
      loadingMore: () => false,
      error: () => null,
      loadRackDrift: () => undefined,
      loadMore: () => undefined,
    };

    await TestBed.configureTestingModule({
      imports: [DriftReportsListComponent],
      providers: [
        { provide: DriftReportStateService, useValue: stateStub },
        {
          provide: ActivatedRoute,
          useValue: {
            paramMap: of({ get: () => 'rack-1' }),
            queryParamMap: of({ get: () => null }),
          },
        },
        { provide: Router, useValue: { navigate: () => Promise.resolve(true) } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(DriftReportsListComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  });

  it('has no automatically-detectable accessibility violations', async () => {
    const results = await axe.run(fixture.nativeElement, {
      rules: { 'color-contrast': { enabled: false } },
    });

    expect(results.violations).toEqual([]);
  }, 15000);
});
