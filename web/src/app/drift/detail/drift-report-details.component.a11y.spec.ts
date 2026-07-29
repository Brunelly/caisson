// Automated accessibility check for the drift item detail view (NFR5), including the embedded
// ApplyActionComponent slot — both the RBAC-hidden explanation and the enabled Apply button variants
// are checked. `color-contrast` is disabled for the same jsdom-has-no-paint-engine reason as
// topology-page.a11y.spec.ts; the real-browser contrast pass lives in web/e2e/drift-harness.spec.ts.
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import axe from 'axe-core';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { Subject, of } from 'rxjs';
import { describe, expect, it } from 'vitest';
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

async function renderWithRoles(
  roles: string[],
): Promise<ComponentFixture<DriftReportDetailsComponent>> {
  const paramMap$ = new Subject<ReturnType<typeof convertToParamMap>>();

  await TestBed.configureTestingModule({
    imports: [DriftReportDetailsComponent],
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: ActivatedRoute, useValue: { paramMap: paramMap$.asObservable() } },
      {
        provide: OidcSecurityService,
        useValue: { getPayloadFromAccessToken: () => of({ roles }) },
      },
      {
        provide: TopologySignalRService,
        useValue: {
          connect: () => undefined,
          disconnect: () => undefined,
          trackJob: () => undefined,
        },
      },
      {
        provide: DriftReportService,
        useValue: { getItemById: () => of({ kind: 'ok', value: item() }) },
      },
    ],
  }).compileComponents();

  const fixture = TestBed.createComponent(DriftReportDetailsComponent);
  fixture.detectChanges();
  paramMap$.next(convertToParamMap({ rackId: 'rack-1', driftItemId: 'item-1' }));
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return fixture;
}

describe('DriftReportDetailsComponent accessibility', () => {
  it('has no automatically-detectable accessibility violations without the DriftApply permission (RBAC-hidden explanation)', async () => {
    const fixture = await renderWithRoles(['ReadOnly']);

    const results = await axe.run(fixture.nativeElement, {
      rules: { 'color-contrast': { enabled: false } },
    });

    expect(results.violations).toEqual([]);
  }, 15000);

  it('has no automatically-detectable accessibility violations with the DriftApply permission (enabled Apply button)', async () => {
    const fixture = await renderWithRoles(['ReadOnly', 'DriftApply']);

    const results = await axe.run(fixture.nativeElement, {
      rules: { 'color-contrast': { enabled: false } },
    });

    expect(results.violations).toEqual([]);
  }, 15000);
});
