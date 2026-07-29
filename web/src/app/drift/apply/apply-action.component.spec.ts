import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Subject, of } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { DriftPermissionService } from '../../core/auth/drift-permission.service';
import { TelemetryService } from '../../core/telemetry/telemetry.service';
import { ToastService } from '../../shared/toast/toast.service';
import { TopologySignalRService } from '../../topology/live/topology-signalr.service';
import { DriftApplyJobStatusService } from '../live/drift-apply-job-status.service';
import type { DriftItemDto } from '../model/drift-contracts';
import type { ApplyDriftCorrectionResult } from '../services/drift-apply.service';
import { DriftApplyService } from '../services/drift-apply.service';
import { DriftReportService } from '../services/drift-report.service';
import { ApplyActionComponent } from './apply-action.component';

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
    details: { switchName: 'sw-01', portName: 'ether5' },
    createdAt: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

@Component({
  standalone: true,
  imports: [ApplyActionComponent],
  template: `<app-apply-action
    [item]="item()"
    [rackId]="rackId"
    (jobCreated)="onJobCreated($event)"
    (refreshRequested)="onRefreshRequested()"
  />`,
})
class HostComponent {
  readonly item = signal<DriftItemDto>(item());
  readonly rackId = 'rack-1';
  readonly jobCreatedIds: string[] = [];
  readonly refreshRequestedCount = { value: 0 };

  onJobCreated(jobId: string): void {
    this.jobCreatedIds.push(jobId);
  }

  onRefreshRequested(): void {
    this.refreshRequestedCount.value++;
  }
}

describe('ApplyActionComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let applyCorrection: ReturnType<typeof vi.fn>;
  let getItemById: ReturnType<typeof vi.fn>;
  let toastSuccess: ReturnType<typeof vi.fn>;
  let toastError: ReturnType<typeof vi.fn>;

  function setup(canApplyDrift: boolean) {
    applyCorrection = vi.fn(() =>
      of<ApplyDriftCorrectionResult>({ kind: 'created', jobId: 'job-1' }),
    );
    getItemById = vi.fn(() => of({ kind: 'ok', value: item() }));
    toastSuccess = vi.fn();
    toastError = vi.fn();

    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        {
          provide: DriftPermissionService,
          useValue: { canApplyDrift: signal(canApplyDrift) },
        },
        { provide: DriftApplyService, useValue: { applyCorrection } },
        { provide: DriftReportService, useValue: { getItemById } },
        { provide: ToastService, useValue: { success: toastSuccess, error: toastError } },
        { provide: TelemetryService, useValue: new TelemetryService() },
        { provide: TopologySignalRService, useValue: { trackJob: vi.fn() } },
        { provide: DriftApplyJobStatusService, useValue: { statusFor: () => null } },
      ],
    });

    fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
  }

  afterEach(() => {
    // Any still-open CDK dialog from a test that didn't explicitly close it would otherwise leak into
    // the next test's `document.querySelector('.apply-dialog')` lookups.
    document
      .querySelectorAll('.apply-dialog')
      .forEach((el) => el.closest('.cdk-overlay-pane')?.remove());
  });

  function applyButton(): HTMLButtonElement | null {
    return fixture.nativeElement.querySelector('.apply-action__apply');
  }

  function dialogEl(): HTMLElement | null {
    return document.querySelector('.apply-dialog');
  }

  it('RBAC-hidden: renders no Apply button and an inline explanation naming the DriftApply permission when the claim is absent', () => {
    setup(false);

    expect(applyButton()).toBeNull();
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('DriftApply');
    expect(applyCorrection).not.toHaveBeenCalled();
  });

  it('renders an enabled Apply button for an applyable item when the DriftApply claim is present', () => {
    setup(true);

    const button = applyButton();
    expect(button).toBeTruthy();
    expect(button?.disabled).toBe(false);
  });

  it('opens the confirmation dialog on Apply click, disables Submit until acknowledged, and calls applyCorrection only after confirmation', async () => {
    setup(true);

    applyButton()!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const dialog = dialogEl();
    expect(dialog).toBeTruthy();
    const submit = dialog!.querySelector<HTMLButtonElement>('.apply-dialog__submit');
    expect(submit?.disabled).toBe(true);
    expect(applyCorrection).not.toHaveBeenCalled();

    const checkbox = dialog!.querySelector<HTMLInputElement>('input[type="checkbox"]');
    checkbox!.checked = true;
    checkbox!.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(submit?.disabled).toBe(false);

    submit!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(applyCorrection).toHaveBeenCalledWith('rack-1', 'item-1');
    expect(toastSuccess).toHaveBeenCalled();
    expect(fixture.componentInstance.jobCreatedIds).toEqual(['job-1']);
  });

  it('Cancel makes zero API calls and closes the dialog', async () => {
    setup(true);

    applyButton()!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const cancel = dialogEl()!.querySelector<HTMLButtonElement>('.apply-dialog__cancel');
    cancel!.click();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(dialogEl()).toBeNull();
    expect(applyCorrection).not.toHaveBeenCalled();
  });

  it('Escape makes zero API calls and closes the dialog', async () => {
    setup(true);

    applyButton()!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(dialogEl()).toBeTruthy();
    // CDK Dialog's Escape handler checks the legacy `keyCode` field (ESCAPE = 27), not `key` — jsdom's
    // KeyboardEvent constructor doesn't derive keyCode from `key`, so it must be passed explicitly.
    dialogEl()!.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }),
    );
    fixture.detectChanges();
    await fixture.whenStable();

    expect(dialogEl()).toBeNull();
    expect(applyCorrection).not.toHaveBeenCalled();
  });

  it('double-submit guard: submitting flips synchronously before the HTTP call, and a rapid second confirm triggers exactly one applyCorrection call', async () => {
    const pending = new Subject<ApplyDriftCorrectionResult>();
    setup(true);
    applyCorrection.mockReturnValue(pending);

    applyButton()!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const dialog = dialogEl()!;
    const checkbox = dialog.querySelector<HTMLInputElement>('input[type="checkbox"]')!;
    checkbox.checked = true;
    checkbox.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    dialog.querySelector<HTMLButtonElement>('.apply-dialog__submit')!.click();
    fixture.detectChanges();

    // The apply flow is in flight (pending Subject hasn't emitted). The Apply button must already be
    // disabled — a second click must not fire a second applyCorrection call.
    expect(applyCorrection).toHaveBeenCalledTimes(1);
    const buttonDuringFlight = applyButton();
    expect(buttonDuringFlight?.disabled).toBe(true);
    buttonDuringFlight?.click();
    fixture.detectChanges();

    expect(applyCorrection).toHaveBeenCalledTimes(1);

    pending.next({ kind: 'created', jobId: 'job-1' });
    pending.complete();
    fixture.detectChanges();
  });

  it('422 (unprocessable) marks the item stale, hides Apply, and shows a Refresh affordance', async () => {
    setup(true);
    applyCorrection.mockReturnValue(
      of({ kind: 'unprocessable', reasonCode: 'unsupported-drift-type' }),
    );

    applyButton()!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const dialog = dialogEl()!;
    const checkbox = dialog.querySelector<HTMLInputElement>('input[type="checkbox"]')!;
    checkbox.checked = true;
    checkbox.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    dialog.querySelector<HTMLButtonElement>('.apply-dialog__submit')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(applyButton()).toBeNull();
    const staleBlock = fixture.nativeElement.querySelector('.apply-action__stale');
    expect(staleBlock).toBeTruthy();
    expect(toastError).toHaveBeenCalled();
  });

  it('a stale item shows Refresh, which re-fetches and emits refreshRequested', async () => {
    setup(true);
    applyCorrection.mockReturnValue(
      of({ kind: 'unprocessable', reasonCode: 'unsupported-drift-type' }),
    );
    applyButton()!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const dialog = dialogEl()!;
    dialog.querySelector<HTMLInputElement>('input[type="checkbox"]')!.checked = true;
    dialog
      .querySelector<HTMLInputElement>('input[type="checkbox"]')!
      .dispatchEvent(new Event('change'));
    fixture.detectChanges();
    dialog.querySelector<HTMLButtonElement>('.apply-dialog__submit')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const refreshButton = fixture.nativeElement.querySelector('.apply-action__stale button');
    refreshButton.click();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(fixture.componentInstance.refreshRequestedCount.value).toBe(1);
    expect(getItemById).toHaveBeenCalled();
  });

  it('403 shows an error toast (defence-in-depth) without hiding the Apply button', async () => {
    setup(true);
    applyCorrection.mockReturnValue(of({ kind: 'forbidden' }));

    applyButton()!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const dialog = dialogEl()!;
    dialog.querySelector<HTMLInputElement>('input[type="checkbox"]')!.checked = true;
    dialog
      .querySelector<HTMLInputElement>('input[type="checkbox"]')!
      .dispatchEvent(new Event('change'));
    fixture.detectChanges();
    dialog.querySelector<HTMLButtonElement>('.apply-dialog__submit')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(toastError).toHaveBeenCalledWith(expect.stringContaining('DriftApply'));
  });

  it('429 shows an error toast carrying the correlationId', async () => {
    setup(true);
    applyCorrection.mockReturnValue(of({ kind: 'rateLimited', correlationId: 'corr-429' }));

    applyButton()!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const dialog = dialogEl()!;
    dialog.querySelector<HTMLInputElement>('input[type="checkbox"]')!.checked = true;
    dialog
      .querySelector<HTMLInputElement>('input[type="checkbox"]')!
      .dispatchEvent(new Event('change'));
    fixture.detectChanges();
    dialog.querySelector<HTMLButtonElement>('.apply-dialog__submit')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(toastError).toHaveBeenCalledWith(expect.any(String), 'corr-429');
  });
});
