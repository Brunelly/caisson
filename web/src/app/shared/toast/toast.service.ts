// ADR 0034: the app's first success/error surface. A minimal, signal-backed queue rather than a
// third-party toast library — the app needs exactly two kinds of message (success, error, the latter
// optionally carrying a correlationId for support/debugging, NFR4) with no stacking/animation
// requirements beyond what a plain `@for` in ToastOutletComponent already gives for free.
import { Injectable, signal } from '@angular/core';

export type ToastKind = 'success' | 'error';

export interface ToastMessage {
  id: number;
  kind: ToastKind;
  text: string;
  correlationId: string | null;
}

const SUCCESS_AUTO_DISMISS_MS = 6000;

@Injectable({ providedIn: 'root' })
export class ToastService {
  private readonly _toasts = signal<ToastMessage[]>([]);
  readonly toasts = this._toasts.asReadonly();

  private nextId = 0;

  /** Auto-dismisses after a few seconds — success messages are confirmatory, not actionable, so they
   * shouldn't require a manual dismiss to clear the screen. */
  success(text: string): void {
    const id = this.push('success', text, null);
    setTimeout(() => this.dismiss(id), SUCCESS_AUTO_DISMISS_MS);
  }

  /** Errors stay until manually dismissed — the correlationId (NFR4) is often needed for support and
   * must stay legible, not disappear on a timer. */
  error(text: string, correlationId: string | null = null): void {
    this.push('error', text, correlationId);
  }

  dismiss(id: number): void {
    this._toasts.update((toasts) => toasts.filter((toast) => toast.id !== id));
  }

  private push(kind: ToastKind, text: string, correlationId: string | null): number {
    const id = this.nextId++;
    this._toasts.update((toasts) => [...toasts, { id, kind, text, correlationId }]);
    return id;
  }
}
