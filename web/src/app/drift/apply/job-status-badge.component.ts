// Thin wrapper over shared/badge/status-badge.component.ts (Task #130), mirroring
// drift/shared/drift-severity-badge.component.ts's pattern: an independent DriftApplyJobStatus -> badge
// mapping, reused by the apply/detail view (story #67 step 5) and the audit view (step 6) so the eight
// job statuses never render inconsistently between them. Deliberately NOT a direct bind of `status` onto
// StatusBadgeComponent's mapping-state/confidence/severity kinds — pending/success/error is a different
// axis entirely (a job can be "success" while its subject stays "unmapped") — see
// shared/badge/status-badge.component.ts's `JobStatusBadgeKind`.
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { StatusBadgeComponent } from '../../shared/badge/status-badge.component';
import type { JobStatusBadgeKind } from '../../shared/badge/status-badge.component';
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

// NFR5: never colour-only. Kept more granular than the 3-bucket badge `kind` (e.g. Revalidating/
// Executing get a distinct "in progress" glyph from Pending/Claimed's "waiting" glyph) via
// StatusBadgeComponent's `iconOverride` input.
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

const BADGE_KIND: Record<DriftApplyJobStatus, JobStatusBadgeKind> = {
  Pending: 'job-pending',
  Claimed: 'job-pending',
  Revalidating: 'job-pending',
  Executing: 'job-pending',
  Completed: 'job-success',
  Failed: 'job-error',
  StaleDrift: 'job-error',
  Canceled: 'job-error',
};

@Component({
  selector: 'app-job-status-badge',
  standalone: true,
  imports: [StatusBadgeComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-status-badge [kind]="badgeKind()" [labelText]="label()" [iconOverride]="icon()" />
  `,
})
export class JobStatusBadgeComponent {
  readonly status = input.required<DriftApplyJobStatus>();

  protected readonly badgeKind = computed(() => BADGE_KIND[this.status()]);
  protected readonly label = computed(() => LABELS[this.status()]);
  protected readonly icon = computed(() => ICONS[this.status()]);
}
