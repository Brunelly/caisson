// ADR 0034: @angular/cdk/dialog's Dialog is the modal primitive (focus-trap, role="dialog"/
// aria-modal, Escape-to-close, backdrop, focus restored to the trigger — all for free), distinct from
// topology-search.component.ts's anchored CdkConnectedOverlay combobox pattern. Cancel, Escape and a
// backdrop click all resolve DialogRef.closed with no value and make ZERO API calls — only an explicit
// Submit click (gated by the acknowledgement checkbox) closes with 'submit', which the caller
// (apply-action.component.ts) is the only thing that ever triggers the actual applyCorrection call.
import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { Component, inject, signal } from '@angular/core';
import type { DriftItemDto } from '../model/drift-contracts';
import { DriftSeverityBadgeComponent } from '../shared/drift-severity-badge.component';

export interface ApplyConfirmationDialogData {
  item: DriftItemDto;
}

export type ApplyConfirmationDialogResult = 'submit' | undefined;

@Component({
  selector: 'app-apply-confirmation-dialog',
  standalone: true,
  imports: [DriftSeverityBadgeComponent],
  styleUrl: './apply-confirmation-dialog.component.scss',
  template: `
    <div class="apply-dialog">
      <h2 id="apply-dialog-heading" class="apply-dialog__heading">Apply drift correction</h2>

      <dl class="apply-dialog__summary">
        <dt>Target</dt>
        <dd>{{ data.item.subjectType }}: {{ data.item.subjectKey }}</dd>
        <dt>Current → desired</dt>
        <dd>{{ data.item.actualValue ?? '—' }} → {{ data.item.expectedValue ?? '—' }}</dd>
        <dt>Drift type</dt>
        <dd>{{ data.item.driftType }}</dd>
        <dt>Severity</dt>
        <dd><app-drift-severity-badge [severity]="data.item.severity" /></dd>
      </dl>

      <p class="apply-dialog__rollback">{{ rollbackWindowText }}</p>

      <p class="apply-dialog__write-warning">
        This is a write operation and may trigger automatic rollback behaviour on the switch.
      </p>

      <label class="apply-dialog__ack">
        <input type="checkbox" [checked]="acknowledged()" (change)="onAcknowledgeChange($event)" />
        I acknowledge this action changes device configuration.
      </label>

      <div class="apply-dialog__actions">
        <button type="button" class="apply-dialog__cancel" (click)="cancel()">Cancel</button>
        <button
          type="button"
          class="apply-dialog__submit"
          [disabled]="!acknowledged()"
          (click)="submit()"
        >
          Apply
        </button>
      </div>
    </div>
  `,
})
export class ApplyConfirmationDialogComponent {
  protected readonly data = inject<ApplyConfirmationDialogData>(DIALOG_DATA);
  private readonly dialogRef = inject(DialogRef<ApplyConfirmationDialogResult>);

  protected readonly acknowledged = signal(false);

  /** Sourced from the drift item's details bag if the backend supplied it; never the story's
   * illustrative 120s default — no read API currently returns the rollback window duration (ADR 0034,
   * a documented backend follow-up), so a non-numeric fallback line is shown instead. */
  protected readonly rollbackWindowText: string = rollbackWindowTextFor(this.data.item);

  protected onAcknowledgeChange(event: Event): void {
    this.acknowledged.set((event.target as HTMLInputElement).checked);
  }

  protected cancel(): void {
    this.dialogRef.close(undefined);
  }

  protected submit(): void {
    if (!this.acknowledged()) {
      return;
    }
    this.dialogRef.close('submit');
  }
}

export function rollbackWindowTextFor(item: DriftItemDto): string {
  const details = item.details;
  const seconds =
    details && typeof details === 'object'
      ? (details as Record<string, unknown>)['rollbackWindowSeconds']
      : undefined;
  if (typeof seconds === 'number' && Number.isFinite(seconds)) {
    return `Confirmed-commit rollback window: ${seconds} seconds.`;
  }
  return 'Protected by an automatic confirmed-commit rollback if not confirmed by the switch.';
}
