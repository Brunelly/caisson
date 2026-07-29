// Shared DriftApplyJobStatus -> label/token/icon mapping, reused by the apply/detail view (story #67
// step 5) and the audit view (step 6) so the eight job statuses never render inconsistently between
// them. Token-driven only (NFR5: icon + text, never colour alone), mirroring shared/badge/
// status-badge.component.ts's data-driven-Record convention without collapsing into that component —
// the job-status vocabulary (pending/terminal-success/terminal-failure) is a different axis entirely
// from mapping-state/confidence/severity.
import { Component, computed, input } from '@angular/core';
import type { DriftApplyJobStatus } from '../model/drift-contracts';

const LABELS: Record<DriftApplyJobStatus, string> = {
  Pending: 'Pending',
  Claimed: 'Claimed',
  Revalidating: 'Revalidating',
  Executing: 'Executing',
  Completed: 'Completed',
  Failed: 'Failed',
  StaleDrift: 'Stale drift — rejected',
  Canceled: 'Canceled',
};

const ICONS: Record<DriftApplyJobStatus, string> = {
  Pending: '…',
  Claimed: '…',
  Revalidating: '↻',
  Executing: '↻',
  Completed: '✓',
  Failed: '✕',
  StaleDrift: '✕',
  Canceled: '✕',
};

type JobStatusCssKind = 'pending' | 'success' | 'error';

const CSS_KIND: Record<DriftApplyJobStatus, JobStatusCssKind> = {
  Pending: 'pending',
  Claimed: 'pending',
  Revalidating: 'pending',
  Executing: 'pending',
  Completed: 'success',
  Failed: 'error',
  StaleDrift: 'error',
  Canceled: 'error',
};

@Component({
  selector: 'app-job-status-badge',
  standalone: true,
  template: `
    <span class="job-status-badge" [class]="cssClass()">
      <span class="job-status-badge__icon" aria-hidden="true">{{ icon() }}</span>
      {{ label() }}
    </span>
  `,
  styles: [
    `
      .job-status-badge {
        display: inline-flex;
        align-items: center;
        gap: 0.25rem;
        border-radius: 999px;
        padding: 0.125rem 0.625rem;
        font-size: 0.75rem;
        font-weight: 600;
        line-height: 1.4;
        white-space: nowrap;
      }

      .job-status-badge--pending {
        background: var(--color-bg-elevated);
        color: var(--color-text-muted);
      }

      .job-status-badge--success {
        background: var(--color-status-confirmed-bg);
        color: var(--color-status-confirmed);
      }

      .job-status-badge--error {
        background: var(--color-status-unmapped-bg);
        color: var(--color-status-unmapped);
      }
    `,
  ],
})
export class JobStatusBadgeComponent {
  readonly status = input.required<DriftApplyJobStatus>();

  protected readonly label = computed(() => LABELS[this.status()]);
  protected readonly icon = computed(() => ICONS[this.status()]);
  protected readonly cssClass = computed(() => `job-status-badge--${CSS_KIND[this.status()]}`);
}
