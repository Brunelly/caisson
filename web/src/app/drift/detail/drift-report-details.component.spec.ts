import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { Subject, of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { TopologySignalRService } from '../../topology/live/topology-signalr.service';
import type { DriftItemDto } from '../model/drift-contracts';
import { DriftReportService } from '../services/drift-report.service';
import { DriftReportDetailsComponent } from './drift-report-details.component';

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
    why: 'Access VLAN mismatch on sw-01/ether5',
    details: { switchName: 'sw-01', portName: 'ether5' },
    createdAt: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

describe('DriftReportDetailsComponent', () => {
  let fixture: ComponentFixture<DriftReportDetailsComponent>;
  let getItemById: ReturnType<typeof vi.fn>;
  let paramMap$: Subject<ReturnType<typeof convertToParamMap>>;

  beforeEach(async () => {
    paramMap$ = new Subject();

    await TestBed.configureTestingModule({
      imports: [DriftReportDetailsComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: { paramMap: paramMap$.asObservable() } },
        // ApplyActionComponent (hosted in the apply slot) transitively needs OidcSecurityService via
        // DriftPermissionService — no permission by default, so the Apply button doesn't render here.
        { provide: OidcSecurityService, useValue: { getPayloadFromAccessToken: () => of({}) } },
        // Real TopologySignalRService would build a real HubConnection and attempt to actually
        // connect() — stub it, mirroring topology-page.component.spec.ts's pattern.
        {
          provide: TopologySignalRService,
          useValue: { connect: vi.fn(), disconnect: vi.fn(), trackJob: vi.fn() },
        },
      ],
    }).compileComponents();
  });

  function createWith(getItemByIdImpl: ReturnType<typeof vi.fn>) {
    getItemById = getItemByIdImpl;
    TestBed.overrideProvider(DriftReportService, { useValue: { getItemById } });
    fixture = TestBed.createComponent(DriftReportDetailsComponent);
  }

  it('renders why, drift type, severity badge and before/after once the item loads', () => {
    createWith(vi.fn(() => of({ kind: 'ok', value: item() })));
    fixture.detectChanges();

    paramMap$.next(convertToParamMap({ rackId: 'rack-1', driftItemId: 'item-1' }));
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Access VLAN mismatch on sw-01/ether5');
    expect(text).toContain('AccessVlanMismatch');
    expect(text).toContain('High severity');
    expect(text).toContain('100');
    expect(text).toContain('200');
  });

  it('renders the details bag as labelled key/value pairs', () => {
    createWith(vi.fn(() => of({ kind: 'ok', value: item() })));
    fixture.detectChanges();

    paramMap$.next(convertToParamMap({ rackId: 'rack-1', driftItemId: 'item-1' }));
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('switchName');
    expect(text).toContain('sw-01');
    expect(text).toContain('portName');
    expect(text).toContain('ether5');
  });

  it('applies the DS monospace/tabular-numeral identifier class to the subject key, before/after values, and identifier detail-bag entries, but not to other detail-bag entries (Task #129)', () => {
    createWith(
      vi.fn(() =>
        of({
          kind: 'ok',
          value: item({
            details: { switchName: 'sw-01', portName: 'ether5', note: 'manual override' },
          }),
        }),
      ),
    );
    fixture.detectChanges();
    paramMap$.next(convertToParamMap({ rackId: 'rack-1', driftItemId: 'item-1' }));
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('h1 .drift-detail__identifier')?.textContent).toBe(
      'v1|rack|sw-01|ether5',
    );
    expect(
      el.querySelectorAll('.drift-detail__before-after .drift-detail__identifier').length,
    ).toBe(2);

    function ddFor(key: string): Element | null {
      const dt = Array.from(el.querySelectorAll('.drift-detail__details dt')).find(
        (node) => node.textContent === key,
      );
      return dt?.nextElementSibling ?? null;
    }

    expect(ddFor('switchName')?.classList.contains('drift-detail__identifier')).toBe(true);
    expect(ddFor('portName')?.classList.contains('drift-detail__identifier')).toBe(true);
    expect(ddFor('note')?.classList.contains('drift-detail__identifier')).toBe(false);
  });

  it('shows a status region while loading', () => {
    createWith(vi.fn(() => new Subject()));
    fixture.detectChanges();

    paramMap$.next(convertToParamMap({ rackId: 'rack-1', driftItemId: 'item-1' }));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[role="status"]')).toBeTruthy();
  });

  it('shows a not-found status message for a notFound result', () => {
    createWith(vi.fn(() => of({ kind: 'notFound' })));
    fixture.detectChanges();

    paramMap$.next(convertToParamMap({ rackId: 'rack-1', driftItemId: 'missing' }));
    fixture.detectChanges();

    const status = fixture.nativeElement.querySelector('[role="status"]');
    expect(status?.textContent).toContain('could not be found');
  });

  it('shows an alert for a generic error result', () => {
    createWith(vi.fn(() => of({ kind: 'error', status: 500 })));
    fixture.detectChanges();

    paramMap$.next(convertToParamMap({ rackId: 'rack-1', driftItemId: 'item-1' }));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[role="alert"]')).toBeTruthy();
  });

  it('refresh() re-fetches the current item (the stale-drift 422 manual refresh affordance)', () => {
    createWith(vi.fn(() => of({ kind: 'ok', value: item() })));
    fixture.detectChanges();
    paramMap$.next(convertToParamMap({ rackId: 'rack-1', driftItemId: 'item-1' }));
    getItemById.mockClear();

    fixture.componentInstance.refresh();

    expect(getItemById).toHaveBeenCalledWith('rack-1', 'item-1');
  });
});
