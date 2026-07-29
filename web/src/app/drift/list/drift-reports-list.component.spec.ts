import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { Subject } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { DriftItemDto } from '../model/drift-contracts';
import { DriftReportStateService, EMPTY_DRIFT_FILTERS } from '../state/drift-report-state.service';
import {
  driftFiltersFromQueryParamMap,
  driftFiltersToQueryParams,
} from './drift-reports-list.component';
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

describe('driftFiltersFromQueryParamMap / driftFiltersToQueryParams', () => {
  it('parses recognised severity/driftType/actionable query params', () => {
    const query = convertToParamMap({
      severity: 'High',
      driftType: 'AccessVlanMismatch',
      actionable: 'true',
    });

    expect(driftFiltersFromQueryParamMap(query)).toEqual({
      severity: 'High',
      driftType: 'AccessVlanMismatch',
      actionable: true,
    });
  });

  it('falls back to null for missing/unrecognised query param values', () => {
    const query = convertToParamMap({ severity: 'NotAReal Severity' });

    expect(driftFiltersFromQueryParamMap(query)).toEqual(EMPTY_DRIFT_FILTERS);
  });

  it('round-trips filters back into query params, using null to remove a param', () => {
    expect(
      driftFiltersToQueryParams({ severity: 'Low', driftType: null, actionable: false }),
    ).toEqual({ severity: 'Low', driftType: null, actionable: 'false' });
  });
});

describe('DriftReportsListComponent', () => {
  let fixture: ComponentFixture<DriftReportsListComponent>;
  let loadRackDrift: ReturnType<typeof vi.fn>;
  let navigate: ReturnType<typeof vi.fn>;
  let paramMap$: Subject<ReturnType<typeof convertToParamMap>>;
  let queryParamMap$: Subject<ReturnType<typeof convertToParamMap>>;

  beforeEach(async () => {
    loadRackDrift = vi.fn();
    navigate = vi.fn(() => Promise.resolve(true));
    paramMap$ = new Subject();
    queryParamMap$ = new Subject();

    const stateStub = {
      rackId: () => 'rack-1',
      report: () => null,
      items: () => [item()],
      nextCursor: () => null,
      filters: () => EMPTY_DRIFT_FILTERS,
      jobStatusByDriftItemId: () => new Map([['item-1', 'Executing']]),
      loading: () => false,
      loadingMore: () => false,
      error: () => null,
      loadRackDrift,
      loadMore: vi.fn(),
    };

    await TestBed.configureTestingModule({
      imports: [DriftReportsListComponent],
      providers: [
        { provide: DriftReportStateService, useValue: stateStub },
        {
          provide: ActivatedRoute,
          useValue: {
            paramMap: paramMap$.asObservable(),
            queryParamMap: queryParamMap$.asObservable(),
          },
        },
        { provide: Router, useValue: { navigate } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(DriftReportsListComponent);
  });

  it('loads the rack drift report for the initial route params/query params', () => {
    fixture.detectChanges();
    paramMap$.next(convertToParamMap({ rackId: 'rack-1' }));
    queryParamMap$.next(convertToParamMap({ severity: 'High' }));

    expect(loadRackDrift).toHaveBeenCalledWith('rack-1', {
      severity: 'High',
      driftType: null,
      actionable: null,
    });
  });

  it('renders the derived job status from the state service, joined by driftItemId', () => {
    fixture.detectChanges();
    paramMap$.next(convertToParamMap({ rackId: 'rack-1' }));
    queryParamMap$.next(convertToParamMap({}));
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Executing');
  });

  it('renders "—" for a drift item with no matching apply job', () => {
    fixture.detectChanges();
    paramMap$.next(convertToParamMap({ rackId: 'rack-1' }));
    queryParamMap$.next(convertToParamMap({}));

    const statusFor = (
      fixture.componentInstance as unknown as { statusFor(i: DriftItemDto): string }
    ).statusFor(item({ driftItemId: 'no-job-item' }));
    expect(statusFor).toBe('—');
  });

  it('navigates with a merged severity query param on filter change (state survives navigation)', () => {
    fixture.detectChanges();
    paramMap$.next(convertToParamMap({ rackId: 'rack-1' }));
    queryParamMap$.next(convertToParamMap({}));
    fixture.detectChanges();

    const select: HTMLSelectElement = fixture.nativeElement.querySelector('#drift-filter-severity');
    select.value = 'High';
    select.dispatchEvent(new Event('change'));

    expect(navigate).toHaveBeenCalledWith(
      [],
      expect.objectContaining({
        queryParams: { severity: 'High', driftType: null, actionable: null },
        queryParamsHandling: 'merge',
      }),
    );
  });
});
