// Pure idempotency gate for live topology events (NFR2, docs/live-topology-events.md rule 3):
// "de-duplicate and never regress" — an event whose stream `seq` is not strictly greater than the last
// one accepted for that stream is dropped, regardless of `eventId`. Per-stream watermarks (one per
// rackId for the snapshot stream, one per jobId for the job stream) live in a plain Map the caller owns,
// so this stays a dependency-free function safe to exhaustively unit test.
export interface StreamWatermark {
  seq: number;
  eventId: string;
}

export type WatermarkStore = Map<string, StreamWatermark>;

export function snapshotStreamKey(rackId: string): string {
  return `snapshot:${rackId}`;
}

export function jobStreamKey(jobId: string): string {
  return `job:${jobId}`;
}

/** A distinctly-namespaced sibling to jobStreamKey (story #67) — DriftApplyJobStatusChanged rides the
 * same hub/watermark store as DiscoveryJobStatusChanged, but keeping the two prefixes distinct avoids
 * any ambiguity between a discovery job id and a drift-apply job id ever colliding in the same map. */
export function driftApplyJobStreamKey(jobId: string): string {
  return `driftApplyJob:${jobId}`;
}

// Story #173: PR status events carry a cluster-monotonic per-link seq (TopologyStreams.ForPullRequest),
// so the client de-dups on the owning PR-link id.
export function prStatusStreamKey(pullRequestLinkId: string): string {
  return `prStatus:${pullRequestLinkId}`;
}

// Finding #2 (client half): mirrors the server-side plausibility check
// (RedisTopologyEventSubscriber.IsPlausibleAsync) at the same threshold. A forward jump this large is
// more plausibly a forged/corrupted event than real traffic even after the server's own HMAC/plausibility
// gates — defense in depth, since this client trusts the hub connection itself as already-authenticated.
export const MAX_PLAUSIBLE_FORWARD_SEQ_JUMP = 10_000;

/**
 * Returns `true` and records the watermark if `candidate` is strictly newer than the last accepted
 * event for `streamKey`; returns `false` (leaving the store untouched) for a duplicate (`seq` equal) or
 * out-of-order/replayed (`seq` lower) event.
 *
 * An implausibly large forward jump is still accepted (so the caller's unconditional `reconcile()` still
 * runs — the safe response to anything unexpected) but the watermark only advances by one: fast-forwarding
 * it to the implausible value would permanently poison the stream, since every subsequent genuine
 * (much lower) seq would then look like a stale duplicate and be silently dropped forever, with no
 * user-visible sign — `resetWatchdog()` fires on every inbound message regardless of acceptance, so the
 * connection would keep reporting "live" while never actually updating again.
 */
export function applyIfNewer(
  store: WatermarkStore,
  streamKey: string,
  candidate: StreamWatermark,
): boolean {
  const current = store.get(streamKey);
  if (current) {
    if (candidate.seq <= current.seq) {
      return false;
    }
    if (candidate.seq - current.seq > MAX_PLAUSIBLE_FORWARD_SEQ_JUMP) {
      store.set(streamKey, { seq: current.seq + 1, eventId: candidate.eventId });
      return true;
    }
  }

  store.set(streamKey, candidate);
  return true;
}
