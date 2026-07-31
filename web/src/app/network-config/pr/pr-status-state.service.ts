// Signal store for rack PR status (story #173, Task #215), cloned from `drift/live/drift-apply-job-status.service.ts`:
// a private signal<ReadonlyMap<rackId, PrStatusDto>> exposed read-only, with `applyEvent()` (optimistic, from
// the SignalR hub) and `applyPolledStatus()` (authoritative, from a REST refetch) both funnelling into `set()`.
import { Injectable, signal } from '@angular/core';
import {
  type GateReasonCode,
  isMergedState,
  type PrStatusChangedEvent,
  type PrStatusDto,
} from './pr-status-contracts';

@Injectable({ providedIn: 'root' })
export class PrStatusStateService {
  private readonly _statusByRackId = signal<ReadonlyMap<string, PrStatusDto>>(new Map());

  /** The full status map (read-only) — exposed for tests/consumers that need reactivity across racks. */
  readonly statusByRackId = this._statusByRackId.asReadonly();

  /** The current PR status for a rack, or null when none has been observed/fetched yet. */
  statusFor(rackId: string): PrStatusDto | null {
    return this._statusByRackId().get(rackId) ?? null;
  }

  /** Optimistically applies a live SignalR event (a REST refetch normally follows and overwrites this). */
  applyEvent(event: PrStatusChangedEvent): void {
    const canApply = isMergedState(event.state);
    const gateReasonCode: GateReasonCode = canApply ? 'Allowed' : 'PrNotMerged';
    this.set(event.rackId, {
      hasPullRequest: true,
      pullRequestNumber: event.pullRequestNumber,
      pullRequestUrl: event.pullRequestUrl,
      state: event.state,
      headSha: event.headSha,
      checksConclusion: event.checksConclusion,
      failingChecksCount: event.failingChecksCount,
      checksSummary: this.statusFor(event.rackId)?.checksSummary ?? null,
      lastUpdated: event.updatedAt,
      lastChecked: event.lastCheckedAt,
      lastPollFailureReason: null,
      canApply,
      gateReasonCode,
    });
  }

  /** Applies the authoritative persisted status from a REST read (always wins — a refetch is current). */
  applyPolledStatus(rackId: string, status: PrStatusDto): void {
    this.set(rackId, status);
  }

  private set(rackId: string, status: PrStatusDto): void {
    this._statusByRackId.update((map) => {
      const next = new Map(map);
      next.set(rackId, status);
      return next;
    });
  }
}
