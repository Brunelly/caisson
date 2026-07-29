// Mounted once in app.ts (ADR 0034), so every feature shares the one success/error surface instead of
// each rolling its own. Success toasts are `role="status"`/`aria-live="polite"` (non-interrupting);
// error toasts are `role="alert"`/`aria-live="assertive"` (interrupting) — screen readers announce
// errors immediately, successes when convenient.
import { Component, inject } from '@angular/core';
import { ToastService } from './toast.service';

@Component({
  selector: 'app-toast-outlet',
  standalone: true,
  styleUrl: './toast-outlet.component.scss',
  template: `
    <div class="toast-outlet">
      @for (toast of toasts.toasts(); track toast.id) {
        <div
          class="toast"
          [class.toast--success]="toast.kind === 'success'"
          [class.toast--error]="toast.kind === 'error'"
          [attr.role]="toast.kind === 'error' ? 'alert' : 'status'"
          [attr.aria-live]="toast.kind === 'error' ? 'assertive' : 'polite'"
        >
          <p class="toast__text">{{ toast.text }}</p>
          @if (toast.correlationId) {
            <p class="toast__correlation">Correlation ID: {{ toast.correlationId }}</p>
          }
          <button
            type="button"
            class="toast__dismiss"
            aria-label="Dismiss notification"
            (click)="toasts.dismiss(toast.id)"
          >
            ✕
          </button>
        </div>
      }
    </div>
  `,
})
export class ToastOutletComponent {
  protected readonly toasts = inject(ToastService);
}
