// Root signal store, parallel to DiscoveryStatusService — the single source every drift-apply UI
// surface reads (ADR 0033). Components NEVER read the raw hub event or a raw poll response directly;
// TopologySignalRService forwards accepted live events here via applyEvent(), and its polling-fallback
// path forwards REST getJob() responses here via applyPolledDetail(). Both funnel into the same
// normalized snapshot shape so a consumer never needs to know which source the latest status came from.
import { Injectable, signal } from '@angular/core';
import type {
  DriftApplyJobDetailDto,
  DriftApplyJobStatus,
  DriftApplyJobStatusChangedEvent,
} from '../model/drift-contracts';

export interface DriftApplyJobStatusSnapshot {
  jobId: string;
  status: DriftApplyJobStatus;
  currentStep: string | null;
  reasonCode: string | null;
  errorCode: string | null;
  correlationId: string | null;
}

@Injectable({ providedIn: 'root' })
export class DriftApplyJobStatusService {
  private readonly _statusByJobId = signal<ReadonlyMap<string, DriftApplyJobStatusSnapshot>>(
    new Map(),
  );
  readonly statusByJobId = this._statusByJobId.asReadonly();

  applyEvent(event: DriftApplyJobStatusChangedEvent): void {
    this.set({
      jobId: event.jobId,
      status: event.status,
      currentStep: event.currentStep,
      reasonCode: event.reasonCode,
      errorCode: event.errorCode,
      correlationId: event.correlationId,
    });
  }

  /** Never trusts the live event summary alone as the final word (docs/live-topology-events.md rule 2,
   * mirrored for drift-apply jobs): the polling-fallback path calls this with a REST getJob() response,
   * which always wins unconditionally — no watermark gate, since a REST refetch is inherently current. */
  applyPolledDetail(detail: DriftApplyJobDetailDto): void {
    this.set({
      jobId: detail.jobId,
      status: detail.status,
      currentStep: detail.currentStep,
      reasonCode: detail.deviceReasonCode,
      errorCode: detail.errorCode,
      correlationId: detail.correlationId,
    });
  }

  statusFor(jobId: string): DriftApplyJobStatusSnapshot | null {
    return this._statusByJobId().get(jobId) ?? null;
  }

  private set(snapshot: DriftApplyJobStatusSnapshot): void {
    this._statusByJobId.update((map) => {
      const next = new Map(map);
      next.set(snapshot.jobId, snapshot);
      return next;
    });
  }
}
