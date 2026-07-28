import { beforeEach, describe, expect, it } from 'vitest';
import {
  type WatermarkStore,
  applyIfNewer,
  jobStreamKey,
  snapshotStreamKey,
} from './apply-if-newer';

describe('applyIfNewer', () => {
  let store: WatermarkStore;

  beforeEach(() => {
    store = new Map();
  });

  it('accepts the first event ever seen for a stream', () => {
    expect(applyIfNewer(store, 'snapshot:rack-1', { seq: 1, eventId: 'e1' })).toBe(true);
    expect(store.get('snapshot:rack-1')).toEqual({ seq: 1, eventId: 'e1' });
  });

  it('accepts a strictly higher seq and advances the watermark', () => {
    applyIfNewer(store, 'snapshot:rack-1', { seq: 5, eventId: 'e1' });
    expect(applyIfNewer(store, 'snapshot:rack-1', { seq: 6, eventId: 'e2' })).toBe(true);
    expect(store.get('snapshot:rack-1')).toEqual({ seq: 6, eventId: 'e2' });
  });

  it('rejects a duplicate delivery with the same seq and the same eventId', () => {
    applyIfNewer(store, 'snapshot:rack-1', { seq: 5, eventId: 'e1' });
    expect(applyIfNewer(store, 'snapshot:rack-1', { seq: 5, eventId: 'e1' })).toBe(false);
    expect(store.get('snapshot:rack-1')).toEqual({ seq: 5, eventId: 'e1' });
  });

  it('rejects a redelivery with the same seq even if eventId differs (seq must strictly increase)', () => {
    applyIfNewer(store, 'snapshot:rack-1', { seq: 5, eventId: 'e1' });
    expect(applyIfNewer(store, 'snapshot:rack-1', { seq: 5, eventId: 'e2' })).toBe(false);
  });

  it('rejects an out-of-order (older) event delivered after a newer one', () => {
    applyIfNewer(store, 'snapshot:rack-1', { seq: 10, eventId: 'e2' });
    expect(applyIfNewer(store, 'snapshot:rack-1', { seq: 9, eventId: 'e1' })).toBe(false);
    // The watermark must not regress.
    expect(store.get('snapshot:rack-1')).toEqual({ seq: 10, eventId: 'e2' });
  });

  it('never regresses across a long replay/reorder burst', () => {
    const sequence = [1, 2, 3, 2, 4, 3, 5, 5, 6];
    const accepted = sequence.map((seq) =>
      applyIfNewer(store, 'snapshot:rack-1', { seq, eventId: `e${seq}` }),
    );
    expect(accepted).toEqual([true, true, true, false, true, false, true, false, true]);
    expect(store.get('snapshot:rack-1')?.seq).toBe(6);
  });

  it('tracks independent watermarks per stream key (different racks do not interfere)', () => {
    applyIfNewer(store, snapshotStreamKey('rack-1'), { seq: 10, eventId: 'e1' });
    expect(applyIfNewer(store, snapshotStreamKey('rack-2'), { seq: 1, eventId: 'e2' })).toBe(true);
    expect(store.get(snapshotStreamKey('rack-1'))?.seq).toBe(10);
    expect(store.get(snapshotStreamKey('rack-2'))?.seq).toBe(1);
  });

  it('tracks the job stream independently from the snapshot stream for the same id', () => {
    applyIfNewer(store, snapshotStreamKey('abc'), { seq: 10, eventId: 'e1' });
    expect(applyIfNewer(store, jobStreamKey('abc'), { seq: 1, eventId: 'e2' })).toBe(true);
  });

  it('snapshotStreamKey/jobStreamKey never collide for the same raw id', () => {
    expect(snapshotStreamKey('abc')).not.toBe(jobStreamKey('abc'));
  });
});
