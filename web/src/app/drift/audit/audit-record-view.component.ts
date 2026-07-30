// Routed at /racks/:rackId/drift/jobs/:jobId — AC6's stable, shareable URL, readable by any recognised
// (TopologyRead) role, NOT apply-gated: an Auditor/ReadOnly principal can review remediation history
// without ever holding DriftApply. Linked from the detail view's Pending/Running state (story #67 step
// 5) and the list's status column. Entirely read-only.
//
// Primary data source is getJob() -> DriftApplyJobDetailDto, which already carries everything AC6 lists
// (actor, timestamps, correlationId/jobId, target, before/after, outcome, rollback details, the
// steps[] timeline). There is no dedicated per-apply audit endpoint (ADR 0033/0034) — the secondary
// audit-trail list comes from the generic AuditController, windowed by the job's requested/finished
// timestamps and filtered client-side to targetType === 'drift-apply-job' && targetId === jobId (the
// exact values Caisson.Api.Controllers.DriftApplyController/DriftApplyJobRunner write).
import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import type { AuditEventDto } from '../../topology/model/topology-contracts';
import { AuditService } from '../../topology/services/audit.service';
import type { JobStatusBadgeKind } from '../../shared/badge/status-badge.component';
import { StatusBadgeComponent } from '../../shared/badge/status-badge.component';
import { JobStatusTimelineComponent } from '../apply/job-status-timeline.component';
import type { DriftApplyJobDetailDto } from '../model/drift-contracts';
import { DriftApplyService } from '../services/drift-apply.service';

export type AuditRecordLoadError =
  'unauthorized' | 'forbidden' | 'notFound' | 'unprocessable' | 'rateLimited' | 'error';

const DRIFT_APPLY_JOB_TARGET_TYPE = 'drift-apply-job';

// AuditEventDto.result is a free-form string written by several unrelated call sites
// (AuditEventWriter.WriteActionAsync/WriteReadAsync across the API) — there is no shared enum on the
// wire to bind to. Task #130: colour-code it via the job-status badge vocabulary using a conservative
// substring classifier; anything unrecognised renders as the neutral "job-pending" bucket rather than
// risk mislabelling an unknown result as success or error.
const RESULT_SUCCESS_HINTS = [
  'success',
  'created',
  'succeeded',
  'completed',
  'enabled',
  'confirmed',
];
const RESULT_ERROR_HINTS = [
  'fail',
  'denied',
  'forbidden',
  'error',
  'conflict',
  'terminal',
  '403',
  '409',
  '500',
];

function auditResultBadgeKind(result: string): JobStatusBadgeKind {
  const normalized = result.toLowerCase();
  if (RESULT_ERROR_HINTS.some((hint) => normalized.includes(hint))) {
    return 'job-error';
  }
  if (RESULT_SUCCESS_HINTS.some((hint) => normalized.includes(hint))) {
    return 'job-success';
  }
  return 'job-pending';
}

@Component({
  selector: 'app-audit-record-view',
  standalone: true,
  imports: [DatePipe, JobStatusTimelineComponent, StatusBadgeComponent],
  styleUrl: './audit-record-view.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="audit-view" role="main">
      @if (loading()) {
        <p role="status">Loading apply job…</p>
      } @else if (error() === 'notFound') {
        <p role="status">This apply job could not be found.</p>
      } @else if (error()) {
        <p role="alert">Something went wrong loading this apply job. Try again shortly.</p>
      } @else if (job(); as jobDetail) {
        <header class="audit-view__header">
          <h1>
            Apply job <span class="audit-view__identifier">{{ jobDetail.jobId }}</span>
          </h1>
          <app-job-status-timeline [status]="jobDetail.status" [steps]="jobDetail.steps" />
        </header>

        <dl class="audit-view__fields">
          <dt>Actor</dt>
          <dd>{{ jobDetail.requestedBy }} ({{ jobDetail.actorType }})</dd>

          <dt>Requested</dt>
          <dd>{{ jobDetail.requestedAt | date: 'medium' }}</dd>

          @if (jobDetail.claimedAt) {
            <dt>Claimed</dt>
            <dd>{{ jobDetail.claimedAt | date: 'medium' }}</dd>
          }

          @if (jobDetail.finishedAt) {
            <dt>Finished</dt>
            <dd>{{ jobDetail.finishedAt | date: 'medium' }}</dd>
          }

          <dt>Correlation ID</dt>
          <dd class="audit-view__identifier">{{ jobDetail.correlationId }}</dd>

          <dt>Target</dt>
          <dd class="audit-view__identifier">
            {{ jobDetail.switchDeviceKey ?? '—' }} / {{ jobDetail.portName ?? '—' }}
          </dd>

          <dt>Before → after</dt>
          <dd>{{ jobDetail.beforeState ?? '—' }} → {{ jobDetail.afterState ?? '—' }}</dd>

          <dt>Outcome</dt>
          <dd>
            {{ jobDetail.status }}
            @if (jobDetail.deviceReasonCode) {
              ({{ jobDetail.deviceReasonCode }})
            }
          </dd>

          @if (jobDetail.deviceConfirmed !== null) {
            <dt>Device confirmed (rollback)</dt>
            <dd>{{ jobDetail.deviceConfirmed ? 'Yes — no rollback' : 'No — auto-rolled back' }}</dd>
          }

          @if (jobDetail.errorMessage) {
            <dt>Error</dt>
            <dd>{{ jobDetail.errorCode }} — {{ jobDetail.errorMessage }}</dd>
          }
        </dl>

        <section class="audit-view__section">
          <h2>Audit trail</h2>
          @if (auditLoading()) {
            <p role="status">Loading audit trail…</p>
          } @else if (auditEvents().length === 0) {
            <p role="status">No audit trail entries found for this job.</p>
          } @else {
            <ul class="audit-view__trail">
              @for (event of auditEvents(); track event.auditEventId) {
                <li>
                  <span class="audit-view__trail-action">{{ event.action }}</span>
                  <app-status-badge
                    [kind]="resultBadgeKind(event.result)"
                    [labelText]="event.result"
                  />
                  <span class="audit-view__trail-date">{{
                    event.occurredAt | date: 'medium'
                  }}</span>
                </li>
              }
            </ul>
          }
        </section>
      }
    </section>
  `,
})
export class AuditRecordViewComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly applyService = inject(DriftApplyService);
  private readonly auditService = inject(AuditService);

  protected readonly job = signal<DriftApplyJobDetailDto | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<AuditRecordLoadError | null>(null);
  protected readonly auditEvents = signal<AuditEventDto[]>([]);
  protected readonly auditLoading = signal(false);

  protected readonly resultBadgeKind = auditResultBadgeKind;

  constructor() {
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe((params) => {
      const rackId = params.get('rackId');
      const jobId = params.get('jobId');
      if (rackId && jobId) {
        this.load(rackId, jobId);
      }
    });
  }

  private load(rackId: string, jobId: string): void {
    this.loading.set(true);
    this.error.set(null);
    this.applyService.getJob(rackId, jobId).subscribe((result) => {
      this.loading.set(false);
      if (result.kind !== 'ok') {
        this.error.set(result.kind);
        this.job.set(null);
        return;
      }
      this.job.set(result.value);
      this.loadAuditTrail(rackId, jobId, result.value);
    });
  }

  private loadAuditTrail(rackId: string, jobId: string, job: DriftApplyJobDetailDto): void {
    this.auditLoading.set(true);
    this.auditService
      .getAudit(rackId, {
        from: job.requestedAt,
        to: job.finishedAt ?? new Date().toISOString(),
        pageSize: 200,
      })
      .subscribe((result) => {
        this.auditLoading.set(false);
        if (result.kind !== 'ok') {
          this.auditEvents.set([]);
          return;
        }
        this.auditEvents.set(
          result.value.items.filter(
            (event) => event.targetType === DRIFT_APPLY_JOB_TARGET_TYPE && event.targetId === jobId,
          ),
        );
      });
  }
}
