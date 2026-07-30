// Shared step-progression visual for a DriftApplyJobStatus, reused by the apply/detail live-status view
// (story #67 step 5) and the audit view (step 6). Without a `steps` input, renders the generic
// Pending -> Revalidating -> Executing stage ladder (all that a live DriftApplyJobStatusChangedEvent or
// a job-summary poll carries); given `steps` (only the full DriftApplyJobDetailDto carries these), the
// audit view gets the precise per-step timeline instead.
import { Component, computed, input } from '@angular/core';
import type { JobStatusBadgeKind } from '../../shared/badge/status-badge.component';
import { StatusBadgeComponent } from '../../shared/badge/status-badge.component';
import type { DriftApplyJobStatus, DriftApplyStepDto } from '../model/drift-contracts';
import { isTerminalDriftApplyJobStatus } from '../model/drift-contracts';
import { JobStatusBadgeComponent } from './job-status-badge.component';

const STAGE_ORDER: readonly DriftApplyJobStatus[] = [
  'Pending',
  'Claimed',
  'Revalidating',
  'Executing',
];

// DriftApplyStepDto.status mirrors Caisson.Domain.Enums.DriftApplyStepStatus.ToString() on the wire
// (Pending/InProgress/Succeeded/Failed/Skipped) — a different, step-scoped vocabulary from the
// job-level DriftApplyJobStatus above. Task #130: colour-coded through the same job-status badge
// buckets as the rest of the app; an unrecognised value falls back to the neutral "job-pending" bucket
// rather than risk mislabelling it.
const STEP_STATUS_BADGE_KIND: Record<string, JobStatusBadgeKind> = {
  Pending: 'job-pending',
  InProgress: 'job-pending',
  Succeeded: 'job-success',
  Failed: 'job-error',
  Skipped: 'job-pending',
};

function stepStatusBadgeKind(status: string): JobStatusBadgeKind {
  return STEP_STATUS_BADGE_KIND[status] ?? 'job-pending';
}

@Component({
  selector: 'app-job-status-timeline',
  standalone: true,
  imports: [JobStatusBadgeComponent, StatusBadgeComponent],
  styleUrl: './job-status-timeline.component.scss',
  template: `
    @if (steps(); as detailedSteps) {
      <ol class="job-timeline" aria-label="Apply job steps">
        @for (step of detailedSteps; track step.stepName) {
          <li class="job-timeline__step">
            <span class="job-timeline__step-name">{{ step.stepName }}</span>
            <app-status-badge [kind]="stepBadgeKind(step.status)" [labelText]="step.status" />
          </li>
        }
      </ol>
    } @else {
      <ol class="job-timeline" aria-label="Apply job progress">
        @for (stage of stageOrder; track stage) {
          <li
            class="job-timeline__stage"
            [class.job-timeline__stage--active]="stage === status()"
            [class.job-timeline__stage--done]="isPastStage(stage)"
          >
            {{ stage }}
          </li>
        }
      </ol>
    }
    <app-job-status-badge [status]="status()" />
  `,
})
export class JobStatusTimelineComponent {
  readonly status = input.required<DriftApplyJobStatus>();
  readonly steps = input<DriftApplyStepDto[] | null>(null);

  protected readonly stageOrder = STAGE_ORDER;
  protected readonly stepBadgeKind = stepStatusBadgeKind;
  private readonly currentStageIndex = computed(() => STAGE_ORDER.indexOf(this.status()));

  protected isPastStage(stage: DriftApplyJobStatus): boolean {
    const stageIndex = STAGE_ORDER.indexOf(stage);
    return stageIndex < this.currentStageIndex() || isTerminalDriftApplyJobStatus(this.status());
  }
}
