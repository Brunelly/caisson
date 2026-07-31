// Wire contracts for the PR status panel (story #173, Task #215), modelled on `drift/model/drift-contracts.ts`:
// camelCase interfaces matching the backend DTOs, the SignalR event shape, and small pure helpers. Kept free
// of Angular/HTTP so it can be imported by services, the store, and specs alike.

/** The rack-scoped PR status projection returned by GET api/racks/{rackId}/git/pull-request. */
export interface PrStatusDto {
  readonly hasPullRequest: boolean;
  readonly pullRequestNumber: number | null;
  readonly pullRequestUrl: string | null;
  readonly state: PrState | null;
  readonly headSha: string | null;
  readonly checksConclusion: ChecksConclusion;
  readonly failingChecksCount: number | null;
  readonly checksSummary: string | null;
  readonly lastUpdated: string | null;
  readonly lastChecked: string | null;
  readonly lastPollFailureReason: string | null;
  readonly canApply: boolean;
  readonly gateReasonCode: GateReasonCode;
}

/** A single PR status transition history entry from GET .../pull-request/events. */
export interface PrStatusEventDto {
  readonly auditEventId: string;
  readonly occurredAt: string;
  readonly action: string;
  readonly actorId: string;
  readonly previousState: string | null;
  readonly newState: string | null;
  readonly previousChecks: string | null;
  readonly newChecks: string | null;
  readonly correlationId: string;
}

/** The SignalR `GitPullRequestStatusChanged` payload (camelCase over the wire). */
export interface PrStatusChangedEvent {
  readonly rackId: string;
  readonly pullRequestLinkId: string;
  readonly repoOwner: string;
  readonly repoName: string;
  readonly pullRequestNumber: number;
  readonly pullRequestUrl: string;
  readonly state: PrState;
  readonly headSha: string | null;
  readonly checksConclusion: ChecksConclusion;
  readonly failingChecksCount: number | null;
  readonly updatedAt: string;
  readonly lastCheckedAt: string;
  readonly seq: number;
  readonly correlationId: string;
}

/** The parsed shape of `PrStatusDto.checksSummary` (a JSON string produced by the backend rollup). */
export interface ChecksRollup {
  readonly conclusion: string;
  readonly checks: readonly ChecksRollupCheck[];
  readonly truncated?: boolean;
}

export interface ChecksRollupCheck {
  readonly name: string;
  readonly status: string;
  readonly conclusion: string;
  readonly detailsUrl?: string;
  readonly started?: string;
  readonly completed?: string;
}

export type PrState = 'Open' | 'Merged' | 'Closed';

export type ChecksConclusion =
  | 'Success'
  | 'Failure'
  | 'Neutral'
  | 'Cancelled'
  | 'Skipped'
  | 'TimedOut'
  | 'ActionRequired'
  | 'Stale'
  | 'Pending'
  | 'Unknown';

/** The gate reason codes shared with the backend (`GitPrGateReasonCodes`). */
export type GateReasonCode = 'Allowed' | 'NoPrLinked' | 'PrNotMerged';

/** Whether a PR state denotes a merged pull request (the apply gate's allow condition). */
export function isMergedState(state: PrState | null | undefined): boolean {
  return state === 'Merged';
}

/** Safely parses the checksSummary JSON string into a rollup, or null when absent/malformed. */
export function parseChecksRollup(checksSummary: string | null | undefined): ChecksRollup | null {
  if (!checksSummary) {
    return null;
  }
  try {
    const parsed = JSON.parse(checksSummary) as ChecksRollup;
    return parsed && Array.isArray(parsed.checks) ? parsed : null;
  } catch {
    return null;
  }
}
