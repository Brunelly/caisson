// The unsaved-changes confirm dialog (story #168, AC4) — a CDK Dialog panel (ADR 0034), the same
// primitive apply-confirmation-dialog.component.ts uses, reused here instead of `window.confirm` so
// focus-trap/Escape/backdrop/ARIA all come from the shared CDK primitive rather than a hand-rolled one.
import { DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';

@Component({
  selector: 'app-discard-changes-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './discard-changes-dialog.component.scss',
  template: `
    <div class="discard-dialog">
      <h2 id="discard-changes-dialog-heading" class="discard-dialog__heading">
        Discard unsaved changes?
      </h2>
      <p class="discard-dialog__body">
        You have unsaved changes to this rack's network intent. Leaving now will discard them.
      </p>
      <div class="discard-dialog__actions">
        <button type="button" class="discard-dialog__cancel" (click)="cancel()">
          Keep editing
        </button>
        <button type="button" class="discard-dialog__discard" (click)="discard()">
          Discard changes
        </button>
      </div>
    </div>
  `,
})
export class DiscardChangesDialogComponent {
  private readonly dialogRef = inject(DialogRef<boolean>);

  protected cancel(): void {
    this.dialogRef.close(false);
  }

  protected discard(): void {
    this.dialogRef.close(true);
  }
}
