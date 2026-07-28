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

/**
 * Returns `true` and records the watermark if `candidate` is strictly newer than the last accepted
 * event for `streamKey`; returns `false` (leaving the store untouched) for a duplicate (`seq` equal) or
 * out-of-order/replayed (`seq` lower) event.
 */
export function applyIfNewer(
  store: WatermarkStore,
  streamKey: string,
  candidate: StreamWatermark,
): boolean {
  const current = store.get(streamKey);
  if (current && candidate.seq <= current.seq) {
    return false;
  }

  store.set(streamKey, candidate);
  return true;
}
