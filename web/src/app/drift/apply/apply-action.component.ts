// Permission-gated Apply action embedded in the drift detail view (AC3/AC4/AC5). Absent (not merely
// disabled) without the DriftApply claim — DriftPermissionService is the single source every Apply
// surface reads (ADR 0033) — with an inline explanation naming the required permission; the server
// remains the sole enforcement point (a 403 here is defence-in-depth for a stale client gate, never the
// primary guard). Disabled-until-settled (`submitting`) is the sole client double-submit guard, layered
// on the backend's one-active-job-per-item dedup (ADR 0032) — there is no client idempotency key.
import { Dialog } from '@angular/cdk/dialog';
import { Component, computed, inject, input, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { DriftPermissionService } from '../../core/auth/drift-permission.service';
import { TelemetryService } from '../../core/telemetry/telemetry.service';
import { ToastService } from '../../shared/toast/toast.service';
import { TopologySignalRService } from '../../topology/live/topology-signalr.service';
import { isTerminalDriftApplyJobStatus } from '../model/drift-contracts';
import type { DriftItemDto } from '../model/drift-contracts';
import { DriftApplyJobStatusService } from '../live/drift-apply-job-status.service';
import { DriftApplyService } from '../services/drift-apply.service';
import { DriftReportService } from '../services/drift-report.service';
import type {
  ApplyConfirmationDialogData,
  ApplyConfirmationDialogResult,
} from './apply-confirmation-dialog.component';
import { ApplyConfirmationDialogComponent } from './apply-confirmation-dialog.component';
import { JobStatusTimelineComponent } from './job-status-timeline.component';

const DRIFT_APPLY_PERMISSION_NAME = 'DriftApply';

@Component({
  selector: 'app-apply-action',
  standalone: true,
  imports: [RouterLink, JobStatusTimelineComponent],
  styleUrl: './apply-action.component.scss',
  template: `
    @if (permission.canApplyDrift()) {
      @if (stale()) {
        <div class="apply-action__stale" role="status">
          <p>
            This drift item may be stale. Refresh to see the latest state before applying a
            correction.
          </p>
          <button type="button" (click)="onRefreshClick()">Refresh</button>
        </div>
      } @else if (isApplyable() && !activeJobId()) {
        <button
          type="button"
          class="apply-action__apply"
          [disabled]="submitting()"
          (click)="onApplyClick()"
        >
          {{ submitting() ? 'Applying…' : 'Apply correction' }}
          <span class="apply-action__write-indicator">write operation</span>
        </button>
      }

      @if (activeJobId(); as jobId) {
        <div class="apply-action__job" role="status">
          <p>
            Apply job
            <a
              class="apply-action__job-link"
              [routerLink]="['/racks', rackId(), 'drift', 'jobs', jobId]"
            >
              {{ jobId }}
            </a>
          </p>
          <app-job-status-timeline [status]="liveStatus() ?? 'Pending'" />
          @if (isTerminal()) {
            <p class="apply-action__outcome">{{ outcomeText() }}</p>
          }
        </div>
      }
    } @else {
      <p class="apply-action__no-permission">
        Applying this correction requires the <strong>DriftApply</strong> permission. Contact an
        administrator to request access — you can still view drift details and audit history.
      </p>
    }
  `,
})
export class ApplyActionComponent {
  readonly item = input.required<DriftItemDto>();
  readonly rackId = input.required<string>();
  /** Fired when the item was independently found stale/gone (422 or a pre-dialog re-check) so the
   * host view (DriftReportDetailsComponent) can re-fetch and refresh its own copy of the item too. */
  readonly refreshRequested = output<void>();
  /** Fired once a job is created/already-active (201/202) — story #67 step 5 wires this to live status. */
  readonly jobCreated = output<string>();

  protected readonly permission = inject(DriftPermissionService);
  private readonly dialog = inject(Dialog);
  private readonly applyService = inject(DriftApplyService);
  private readonly reportService = inject(DriftReportService);
  private readonly toast = inject(ToastService);
  private readonly telemetry = inject(TelemetryService);
  private readonly signalR = inject(TopologySignalRService);
  private readonly jobStatus = inject(DriftApplyJobStatusService);

  protected readonly submitting = signal(false);
  protected readonly stale = signal(false);
  protected readonly activeJobId = signal<string | null>(null);

  /** Reads DriftApplyJobStatusService only — never a raw hub event or poll response directly (ADR
   * 0033). Updates automatically as TopologySignalRService forwards live events / polled results in. */
  protected readonly liveStatus = computed(() => {
    const jobId = this.activeJobId();
    return jobId ? (this.jobStatus.statusFor(jobId)?.status ?? null) : null;
  });

  protected readonly isTerminal = computed(() => {
    const status = this.liveStatus();
    return status !== null && isTerminalDriftApplyJobStatus(status);
  });

  /** Terminal outcomes (Success/Stale drift rejected/Auto-rollback/Failed) are displayed explicitly. */
  protected outcomeText(): string {
    const jobId = this.activeJobId();
    const snapshot = jobId ? this.jobStatus.statusFor(jobId) : null;
    switch (snapshot?.status) {
      case 'Completed':
        return 'Success — the drift correction was applied and confirmed.';
      case 'StaleDrift':
        return 'Rejected — the drift item was stale by the time the job ran. Refresh and re-apply.';
      case 'Failed':
        return snapshot.reasonCode === 'AutoRolledBack'
          ? 'Failed — the change was automatically rolled back (not confirmed in time).'
          : 'Failed — the correction could not be applied.';
      case 'Canceled':
        return 'Canceled.';
      default:
        return '';
    }
  }

  protected isApplyable(): boolean {
    const item = this.item();
    return item.driftType === 'AccessVlanMismatch' && item.actionable;
  }

  protected onApplyClick(): void {
    if (this.submitting()) {
      return;
    }
    const rackId = this.rackId();
    const driftItemId = this.item().driftItemId;

    // Re-fetch immediately before opening the dialog so a since-changed item is caught early, rather
    // than only discovering staleness after the user has already confirmed.
    this.reportService.getItemById(rackId, driftItemId).subscribe((result) => {
      if (result.kind !== 'ok') {
        this.toast.error('Could not refresh this drift item before applying. Try again.');
        return;
      }
      if (result.value.driftType !== 'AccessVlanMismatch' || !result.value.actionable) {
        this.stale.set(true);
        return;
      }
      this.openConfirmationDialog(rackId, result.value);
    });
  }

  protected onRefreshClick(): void {
    this.refreshRequested.emit();
    const rackId = this.rackId();
    this.reportService.getItemById(rackId, this.item().driftItemId).subscribe((result) => {
      if (
        result.kind === 'ok' &&
        result.value.driftType === 'AccessVlanMismatch' &&
        result.value.actionable
      ) {
        this.stale.set(false);
      }
    });
  }

  private openConfirmationDialog(rackId: string, item: DriftItemDto): void {
    const ref = this.dialog.open<ApplyConfirmationDialogResult, ApplyConfirmationDialogData>(
      ApplyConfirmationDialogComponent,
      { data: { item }, ariaLabelledBy: 'apply-dialog-heading', hasBackdrop: true },
    );
    ref.closed.subscribe((result) => {
      if (result === 'submit') {
        this.submit(rackId, item.driftItemId);
      }
    });
  }

  private submit(rackId: string, driftItemId: string): void {
    if (this.submitting()) {
      return;
    }
    // Synchronous, before the HTTP call — the sole client double-submit guard.
    this.submitting.set(true);
    this.telemetry.driftApplyRequested(rackId, driftItemId, null);

    this.applyService.applyCorrection(rackId, driftItemId).subscribe((result) => {
      this.submitting.set(false);

      switch (result.kind) {
        case 'created':
        case 'existingJob':
          this.activeJobId.set(result.jobId);
          this.jobCreated.emit(result.jobId);
          this.signalR.trackJob(result.jobId);
          this.toast.success(
            result.kind === 'created'
              ? 'Drift correction submitted.'
              : 'An apply job for this drift item is already in progress.',
          );
          this.telemetry.driftApplyOutcome(result.jobId, 'submitted', null);
          break;
        case 'unprocessable':
          this.stale.set(true);
          this.toast.error(
            'This drift item is stale and can no longer be applied. Refresh to see the latest state.',
          );
          this.telemetry.driftApplyError('apply', 'unprocessable', null);
          break;
        case 'forbidden':
          this.toast.error(`You do not have the ${DRIFT_APPLY_PERMISSION_NAME} permission.`);
          this.telemetry.driftApplyError('apply', 'forbidden', null);
          break;
        case 'rateLimited':
          this.toast.error('Too many apply requests. Try again shortly.', result.correlationId);
          this.telemetry.driftApplyError('apply', 'rateLimited', result.correlationId);
          break;
        case 'error':
          this.toast.error(
            'Something went wrong submitting this drift correction.',
            result.correlationId,
          );
          this.telemetry.driftApplyError('apply', `error:${result.status}`, result.correlationId);
          break;
        default:
          this.toast.error('Something went wrong submitting this drift correction.');
          this.telemetry.driftApplyError('apply', result.kind, null);
      }
    });
  }
}
