// Create-pull-request acknowledgement dialog (story #170, AC3/AC5). A @angular/cdk/dialog modal (mirrors
// apply-confirmation-dialog.component.ts): focus-trap, role="dialog"/aria-modal, Escape-to-close, backdrop
// and focus-restore all come from the Dialog primitive. It lists every safety warning with a per-warning
// acknowledgement checkbox; submit is disabled until ALL are acknowledged. Cancel, Escape and a backdrop
// click resolve with no value and make ZERO API calls — only an explicit Create pull request click closes
// with the acknowledged codes, which the caller (network-config-shell) turns into the single PR request.
import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import type { ValidationIssue } from '../model/preflight-validation-contracts';

export interface CreatePrDialogData {
  /** The safety warnings that must be acknowledged before a PR can be created. */
  warnings: ValidationIssue[];
}

export type CreatePrDialogResult = { acknowledgedWarningCodes: string[] } | undefined;

interface WarningRow {
  code: string;
  message: string;
}

@Component({
  selector: 'app-create-pr-dialog',
  standalone: true,
  styleUrl: './create-pr-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="create-pr-dialog">
      <header class="create-pr-dialog__header">
        <h2 id="create-pr-dialog-heading" class="create-pr-dialog__heading">Create pull request</h2>
      </header>

      <div class="create-pr-dialog__body">
        @if (rows().length > 0) {
          <p class="create-pr-dialog__intro">
            This change touches
            {{ rows().length }} port{{ rows().length === 1 ? '' : 's' }} that could affect the
            management or uplink path. Acknowledge each safety warning to continue.
          </p>
          <ul class="create-pr-dialog__warnings">
            @for (row of rows(); track row.code) {
              <li class="create-pr-dialog__warning">
                <label class="create-pr-dialog__ack">
                  <input
                    type="checkbox"
                    [checked]="isAcknowledged(row.code)"
                    (change)="onToggle(row.code, $event)"
                  />
                  <span class="create-pr-dialog__warning-text">{{ row.message }}</span>
                </label>
              </li>
            }
          </ul>
        } @else {
          <p class="create-pr-dialog__intro">
            No safety warnings were raised. Create the pull request to propose these changes.
          </p>
        }
      </div>

      <div class="create-pr-dialog__actions">
        <button type="button" class="create-pr-dialog__cancel" (click)="cancel()">Cancel</button>
        <button
          type="button"
          class="create-pr-dialog__submit"
          [disabled]="!allAcknowledged()"
          (click)="submit()"
        >
          Create pull request
        </button>
      </div>
    </div>
  `,
})
export class CreatePrDialogComponent {
  protected readonly data = inject<CreatePrDialogData>(DIALOG_DATA);
  private readonly dialogRef = inject(DialogRef<CreatePrDialogResult>);

  // One row per distinct warning code (a code can recur across ports; acknowledgement is per code).
  protected readonly rows = signal<WarningRow[]>(distinctByCode(this.data.warnings));
  private readonly acknowledged = signal<ReadonlySet<string>>(new Set());

  protected readonly allAcknowledged = computed(() => {
    const acked = this.acknowledged();
    return this.rows().every((row) => acked.has(row.code));
  });

  protected isAcknowledged(code: string): boolean {
    return this.acknowledged().has(code);
  }

  protected onToggle(code: string, event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    this.acknowledged.update((set) => {
      const next = new Set(set);
      if (checked) {
        next.add(code);
      } else {
        next.delete(code);
      }
      return next;
    });
  }

  protected cancel(): void {
    this.dialogRef.close(undefined);
  }

  protected submit(): void {
    if (!this.allAcknowledged()) {
      return;
    }
    this.dialogRef.close({ acknowledgedWarningCodes: this.rows().map((row) => row.code) });
  }
}

function distinctByCode(warnings: ValidationIssue[]): WarningRow[] {
  const seen = new Map<string, WarningRow>();
  for (const warning of warnings) {
    if (!seen.has(warning.code)) {
      seen.set(warning.code, { code: warning.code, message: warning.message });
    }
  }
  return [...seen.values()];
}
