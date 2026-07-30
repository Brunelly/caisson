// Task #133/#134: replaces the plain inline `<span class="discovery-status">` in
// topology-page.component.ts with a small DS-styled glass card, modelled directly on
// drift/apply/job-status-badge.component.ts's status->JobStatusBadgeKind delegation pattern (so the
// same pending/success/error badge vocabulary renders consistently everywhere a job status appears).
// Renders ONLY fields that exist on DiscoveryStatusDto/DiscoveryJobSummaryDto (topology-contracts.ts) —
// no device-progress counter, elapsed/retry timer, or "Job log" link, none of which have backing data or
// a route (AC1/NFR3: no new data surface, visual-only re-skin).
import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { StatusBadgeComponent } from '../../shared/badge/status-badge.component';
import type { JobStatusBadgeKind } from '../../shared/badge/status-badge.component';
import type { DiscoveryStatusDto } from '../model/topology-contracts';

// Caisson.Domain.Enums.DiscoveryJobStatus, serialized as its string name (DiscoveryContractMappers.cs:
// `job.Status.ToString()`) — Queued -> InProgress -> one terminal state (Succeeded/Failed/Canceled).
const BADGE_KIND: Record<string, JobStatusBadgeKind> = {
  Queued: 'job-pending',
  InProgress: 'job-pending',
  Succeeded: 'job-success',
  Failed: 'job-error',
  Canceled: 'job-error',
};

@Component({
  selector: 'app-discovery-job-status-widget',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, StatusBadgeComponent],
  styleUrl: './discovery-job-status-widget.component.scss',
  template: `
    @if (status(); as s) {
      <div class="djs-widget" [class]="'djs-widget--' + accentKind()">
        <div class="djs-widget__accent" aria-hidden="true"></div>
        <div class="djs-widget__body">
          @if (s.latestJob; as job) {
            <app-status-badge [kind]="badgeKind(job.status)" [labelText]="job.status" />
            <span class="djs-widget__job-id">{{ job.jobId }}</span>
            @if (job.errorCode) {
              <span class="djs-widget__error">{{ job.errorCode }}</span>
            }
          } @else {
            <span class="djs-widget__none">No discovery job yet</span>
          }
          @if (s.lastSuccessAt) {
            <span class="djs-widget__meta">Last success {{ s.lastSuccessAt | date: 'short' }}</span>
          }
          @if (s.scheduleEnabled && s.nextRunAt) {
            <span class="djs-widget__meta">Next run {{ s.nextRunAt | date: 'short' }}</span>
          }
        </div>
      </div>
    }
  `,
})
export class DiscoveryJobStatusWidgetComponent {
  readonly status = input<DiscoveryStatusDto | null>(null);

  /** Left-accent colour follows the latest job's badge kind (neutral when there is no job yet). */
  protected readonly accentKind = computed<JobStatusBadgeKind | 'none'>(() => {
    const job = this.status()?.latestJob;
    return job ? this.badgeKind(job.status) : 'none';
  });

  protected badgeKind(status: string): JobStatusBadgeKind {
    return BADGE_KIND[status] ?? 'job-pending';
  }
}
